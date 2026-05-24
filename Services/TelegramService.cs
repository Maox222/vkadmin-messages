using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using vkadmin_msg.Models;

namespace vkadmin_msg.Services
{
    internal class TelegramService : BackgroundService
    {
        private readonly ILogger<TelegramService> _logger;
        private readonly ITelegramBotClient _botClient;
        private readonly ITelegramBotClient? _replyBotClient;
        private readonly long _tgGroupId;
        private readonly MessageBridge _bridge;
        private readonly MultiDataMap _dataMap;
        private readonly AttachmentConverter _converter;
        private readonly IHostApplicationLifetime _appLifetime;

        // ── Буфер медиагрупп (альбомов) ──────────────────────────────────────
        // Telegram присылает фото/видео одного альбома как N отдельных Update
        // с одинаковым MediaGroupId. Накапливаем их 500 мс и пускаем пачкой.
        private readonly ConcurrentDictionary<string, MediaGroupBuffer> _mediaGroupBuffers = new();

        public TelegramService(ILogger<TelegramService> logger, IOptions<BotOptions> options, IServiceProvider serviceProvider, MessageBridge bridge, MultiDataMap dataMap,
            [FromKeyedServices("replyBot")] ITelegramBotClient replyBotClient, ITelegramBotClient clientBot, AttachmentConverter converter, IHostApplicationLifetime appLifetime)
        {
            _logger      = logger;
            _bridge      = bridge;
            _dataMap     = dataMap;
            _converter   = converter;
            _appLifetime = appLifetime;
            _tgGroupId   = options.Value.TelegramBot.AllowedGroupId;
            _botClient   = clientBot;

            if (options.Value.TelegramBot.AllowReply)
                _replyBotClient = replyBotClient;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                new ReceiverOptions { AllowedUpdates = [ Telegram.Bot.Types.Enums.UpdateType.Message, 
                    Telegram.Bot.Types.Enums.UpdateType.EditedMessage, Telegram.Bot.Types.Enums.UpdateType.CallbackQuery ] } ,
                ct);

            _ = ProcessOutgoingTgQueueAsync(ct);

            var me = await _botClient.GetMe(ct);
            _logger.LogInformation("Бот @{Username} запущен.", me.Username);
        }

        private Task HandleErrorAsync(ITelegramBotClient client, Exception exception,
            HandleErrorSource source, CancellationToken ct)
        {
            _logger.LogError("{Error}", JsonConvert.SerializeObject(exception));

            if (exception is ApiRequestException { ErrorCode: 409 })
            {
                _logger.LogCritical("Обнаружен конфликт getUpdates (409). Завершаем приложение...");
                _appLifetime.StopApplication(); 
            }

            return Task.CompletedTask;
        }

        private Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
        {
            switch (update)
            {
                case { Message: { } message }:
                    HandleMessage(message, ct);
                    break;
                case { EditedMessage: { } editedMessage }:
                    HandleEditMessage(editedMessage, ct);
                    break;

                case { CallbackQuery: { } callbackQuery }:
                    _ = HandleCallbackQueryAsync(callbackQuery, ct);
                    break;
            }

            return Task.CompletedTask;
        }

        // ── Редактируемое сообщение от оператора (TG → VK) ───────────────────
        private void HandleEditMessage(Telegram.Bot.Types.Message message, CancellationToken ct)
        {
            if (message.Chat.Id != _tgGroupId) return;
            if (message.MessageThreadId == null || message.MessageThreadId == 1) return;

            if (!HasContent(message)) return;

            _bridge.SendEditToVk((int)message.MessageThreadId, [message]);
        }

        // ── Входящее сообщение от оператора (TG → VK) ────────────────────────

        private void HandleMessage(Telegram.Bot.Types.Message message, CancellationToken ct)
        {
            // Только наша группа и только топики
            if (message.Chat.Id != _tgGroupId) return;
            if (message.MessageThreadId == null || message.MessageThreadId == 1) return;

            if (!HasContent(message)) return;

            // ── Медиагруппа (альбом) ─────────────────────────────────────────
            if (message.MediaGroupId != null)
            {
                var buffer = _mediaGroupBuffers.GetOrAdd(
                    message.MediaGroupId,
                    _ => new MediaGroupBuffer(message.MessageThreadId.Value));

                buffer.Add(message);
                buffer.ResetTimer(() =>
                {
                    _mediaGroupBuffers.TryRemove(message.MediaGroupId, out _);
                    _bridge.SendMessageToVk(buffer.ThreadId, buffer.Messages);
                });

                return;
            }

            _bridge.SendMessageToVk((int)message.MessageThreadId, [message]);
        }

        private bool HasContent(Telegram.Bot.Types.Message message)
        {
            return !string.IsNullOrWhiteSpace(message.Text)
                              || !string.IsNullOrWhiteSpace(message.Caption)
                              || message.Photo != null
                              || message.Document != null
                              || message.Sticker != null
                              //|| message.Audio != null
                              //|| message.Voice != null
                              || message.Video != null
                              || message.VideoNote != null
                              || message.Animation != null;
        }

        private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken ct)
        {
            var data = callbackQuery.Data;
            if (data == null) return;

            if (data.StartsWith("resolve_chat:"))
            {
                if (int.TryParse(data.Split(':')[1].Trim(), out var threadId))
                {
                    bool success = await _bridge.MarkAsReadToVk(threadId, []).WaitAsync(TimeSpan.FromSeconds(5), ct);
                    string answer = success ? "Диалог отмечен как прочитанный" : "Не удалось отметить диалог";
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, answer, cancellationToken: ct);
                }
            }
        }

        // ── Очередь VK → TG (входящие из VK) ────────────────────────────────

        private async Task ProcessOutgoingTgQueueAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var task in _bridge.TgReader.ReadAllAsync(ct))
                {
                    try
                    {
                        switch (task.TgActionType)
                        {
                            case TgActionType.NewMessage:
                                await HandleNewMessageAsync(task, ct);
                                break;

                            case TgActionType.CreateTopicAndMessage:
                                await HandleCreateTopicAsync(task, ct);
                                break;

                            case TgActionType.EditMessage:
                                await HandleEditAsync(task, ct);
                                break;
                        }
                    }
                    catch (ApiRequestException ex) when (ex.ErrorCode == 400)
                    {
                        if (ex.Message.Contains("message thread not found", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogError("[TG] Топик не найден при обработке {Type}", task.TgActionType);
                            var vkUserInfo = _dataMap.GetVkUserInfo(task.VkPeerId);

                            _dataMap.RemoveByPeerId(task.VkPeerId);

                            if (vkUserInfo != null)
                            {
                                _bridge.SendFirstMessageWithTopicToTg(
                                    task.VkPeerId, task.message,
                                    vkUserInfo.firstName ?? "", vkUserInfo.lastName ?? "");
                            }
                        }
                        else if (ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogError("[TG] Чат {GroupId} не найден или бот заблокирован.", _tgGroupId);
                            _appLifetime.StopApplication();
                        }
                        else if (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)) 
                        {
                            _logger.LogError("[TG] Сообщение для редактирования не было найдено");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[TG] Ошибка обработки задачи {Type}", task.TgActionType);
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogInformation("[TG] Воркер очередей Telegram остановлен. {Ex}", ex.Message);
            }
        }

        private ITelegramBotClient GetClient(ToTgMessageTask task) => 
            task.isReply && _replyBotClient != null ? _replyBotClient : _botClient;

        // ── Отредактированное сообщение VK → TG ──────────────────────────────
        private async Task HandleEditAsync(ToTgMessageTask task, CancellationToken ct)
        {
            var tgBot = GetClient(task);

            var (tgThreadId, messages) = _dataMap.GetByPeerId(task.VkPeerId);

            var (_, foundTgMsgId) = messages.FirstOrDefault(
                m => m.VkMsgId == task.message.Id);

            if (foundTgMsgId == 0) return;

            bool hasOnlyText = !string.IsNullOrEmpty(task.message.Text)
                && (task.message.Attachments == null || task.message.Attachments.Count == 0);

            if (hasOnlyText)
            {
                await tgBot.EditMessageText(
                    chatId: _tgGroupId,
                    messageId: foundTgMsgId,
                    text: task.message.Text,
                    cancellationToken: ct);
            }
            else
            {
                await tgBot.EditMessageCaption(
                    chatId: _tgGroupId,
                    messageId: foundTgMsgId,
                    caption: task.message.Text,
                    cancellationToken: ct);
            }
        }

        // ── Новое сообщение VK → TG ─────────────────────────────────────────

        private async Task HandleNewMessageAsync(ToTgMessageTask task, CancellationToken ct)
        {
            var tgBot = GetClient(task);

            var (tgThreadId, messages) = _dataMap.GetByPeerId(task.VkPeerId);

            ReplyParameters? replyPar = null;
            if (task.message.ReplyMessage != null)
            {
                var (_, foundTgMsgId) = messages.FirstOrDefault(
                    m => m.VkMsgId == task.message.ReplyMessage.Id);
                if (foundTgMsgId != 0)
                    replyPar = new ReplyParameters { MessageId = foundTgMsgId, AllowSendingWithoutReply = true };
            }

            bool hasText        = !string.IsNullOrEmpty(task.message.Text);
            bool hasAttachments = task.message.Attachments is { Count: > 0 };

            if (hasAttachments)
            {
                string? captionForAttachments = hasText ? task.message.Text : null;
                var groups = await _converter.ConvertAsync(task.message, captionForAttachments, ct);

                foreach (var group in groups)
                {
                    switch (group.Type)
                    {
                        case AttachmentGroupType.PhotoVideo:
                        case AttachmentGroupType.Document:
                        case AttachmentGroupType.Audio:
                        {
                            var sentMessages = await tgBot.SendMediaGroup(
                                chatId: _tgGroupId,
                                media: group.AlbumMedia!,
                                messageThreadId: tgThreadId,
                                replyParameters: replyPar,
                                cancellationToken: ct);

                            foreach (var sent in sentMessages)
                            {
                                if ((sent.MessageThreadId == 1 || sent.MessageThreadId == null)
                                    && sent.MessageThreadId != tgThreadId)
                                    await DiscardFalseMessageAndThrow(sent.MessageId, ct);

                                _dataMap.Add(task.VkPeerId, tgThreadId,
                                    task.message.Id, sent.MessageId);
                            }
                            break;
                        }

                        case AttachmentGroupType.AudioMessage:
                        {
                            var (file, duration) = group.Voice!.Value;
                            var sent = await tgBot.SendVoice(
                                chatId: _tgGroupId,
                                voice: file,
                                duration: duration,
                                messageThreadId: tgThreadId,
                                replyParameters: replyPar,
                                cancellationToken: ct);

                            if ((sent.MessageThreadId == 1 || sent.MessageThreadId == null)
                                && sent.MessageThreadId != tgThreadId)
                                await DiscardFalseMessageAndThrow(sent.MessageId, ct);

                            _dataMap.Add(task.VkPeerId, tgThreadId,
                                task.message.Id, sent.MessageId);
                            break;
                        }

                        case AttachmentGroupType.ExternalVideo:
                        {
                            var sent = await tgBot.SendMessage(
                                chatId: _tgGroupId,
                                text: $"🎬 {group.ExternalVideoUrl}",
                                messageThreadId: tgThreadId,
                                replyParameters: replyPar,
                                cancellationToken: ct);

                            if ((sent.MessageThreadId == 1 || sent.MessageThreadId == null)
                                && sent.MessageThreadId != tgThreadId)
                                await DiscardFalseMessageAndThrow(sent.MessageId, ct);

                            _dataMap.Add(task.VkPeerId, tgThreadId,
                                task.message.Id, sent.MessageId);
                            break;
                        }

                        case AttachmentGroupType.Sticker:
                        {
                            var sent = await tgBot.SendSticker(
                                chatId: _tgGroupId,
                                sticker: group.StickerFile!,
                                messageThreadId: tgThreadId,
                                replyParameters: replyPar,
                                cancellationToken: ct);

                            if ((sent.MessageThreadId == 1 || sent.MessageThreadId == null)
                                && sent.MessageThreadId != tgThreadId)
                                await DiscardFalseMessageAndThrow(sent.MessageId, ct);

                            _dataMap.Add(task.VkPeerId, tgThreadId,
                                task.message.Id, sent.MessageId);
                            break;
                        }
                    }
                }
            }

            if (hasText && !hasAttachments)
            {
                var sent = await tgBot.SendMessage(
                    chatId: _tgGroupId,
                    text: task.message.Text!,
                    messageThreadId: tgThreadId,
                    replyParameters: replyPar,
                    cancellationToken: ct);

                if ((sent.MessageThreadId == 1 || sent.MessageThreadId == null)
                    && sent.MessageThreadId != tgThreadId)
                    await DiscardFalseMessageAndThrow(sent.MessageId, ct);

                _dataMap.Add(task.VkPeerId, tgThreadId,
                    task.message.Id, sent.MessageId);

                if (task.message.ReplyMessage != null && replyPar == null) 
                {
                    await HandleNewMessageAsync(new ToTgMessageTask(TgActionType.NewMessage, task.VkPeerId, task.message.ReplyMessage), ct);
                }
            }

            if (task.message.ForwardedMessages is { Count: > 0 })
            {
                foreach (var fwd in task.message.ForwardedMessages)
                {
                    var (_, foundTgMsgId) = messages.FirstOrDefault(m => m.VkMsgId == fwd.Id);
                    if (foundTgMsgId == 0 || fwd.Id == null) 
                    {
                        await HandleNewMessageAsync(new ToTgMessageTask(TgActionType.NewMessage, task.VkPeerId, fwd), ct);
                        continue;
                    }

                    var sent = await tgBot.ForwardMessage(
                        chatId: _tgGroupId,
                        fromChatId: _tgGroupId,
                        messageId: foundTgMsgId,
                        messageThreadId: tgThreadId,
                        cancellationToken: ct);

                    _dataMap.Add(task.VkPeerId, tgThreadId, fwd.Id, sent.MessageId);
                }
            }
        }

        private async Task DiscardFalseMessageAndThrow(int messageId, CancellationToken ct)
        {
            await _botClient.DeleteMessage(chatId: _tgGroupId, messageId: messageId, cancellationToken: ct);
            throw new ApiRequestException("message thread not found", 400);
        }

        private async Task HandleCreateTopicAsync(ToTgMessageTask task, CancellationToken ct)
        {
            string topicName = $"{task.firstName} {task.lastName} (VK)";
            _logger.LogInformation("Создаём топик '{Name}'", topicName);

            int[] iconColor = [7322096, 16766590, 13338331, 9367192, 16749490, 16478047];
            var topic = await _botClient.CreateForumTopic(_tgGroupId, topicName, iconColor[Random.Shared.Next(iconColor.Length)], cancellationToken: ct);
            int createdThreadId = topic.MessageThreadId;

            var keyboard = new InlineKeyboardMarkup([[
                InlineKeyboardButton.WithCallbackData(
                    "✅ Отметить прочитанным",
                    $"resolve_chat:{createdThreadId}")
            ]]);

            var sentMsg = await _botClient.SendMessage(
                chatId: _tgGroupId,
                text: $"🤖 Создан новый диалог. https://vk.com/id{task.VkPeerId}.\nИспользуйте кнопку ниже для управления:",
                messageThreadId: createdThreadId,
                replyMarkup: keyboard,
                protectContent: true,
                cancellationToken: ct);

            await _botClient.PinChatMessage(
                chatId: _tgGroupId, 
                messageId: sentMsg.MessageId, 
                disableNotification: true, 
                cancellationToken: ct);

            _dataMap.Add(task.VkPeerId, createdThreadId, null, sentMsg.MessageId,
                task.firstName, task.lastName);

            await HandleNewMessageAsync(task, ct);
        }
    }

    // ── Буфер медиагруппы ─────────────────────────────────────────────────────
    internal sealed class MediaGroupBuffer
    {
        private const int FlushDelayMs = 500;

        private readonly List<Telegram.Bot.Types.Message> _messages = new();
        private readonly object _lock = new();
        private CancellationTokenSource _timerCts = new();

        public int ThreadId { get; }

        public IReadOnlyList<Telegram.Bot.Types.Message> Messages
        {
            get { lock (_lock) return _messages.ToList(); }
        }

        public MediaGroupBuffer(int threadId) => ThreadId = threadId;

        public void Add(Telegram.Bot.Types.Message msg)
        {
            lock (_lock)
                _messages.Add(msg);
        }

        public void ResetTimer(Action onFlush)
        {
            CancellationTokenSource oldCts;
            CancellationTokenSource newCts;

            lock (_lock)
            {
                oldCts    = _timerCts;
                _timerCts = newCts = new CancellationTokenSource();
            }

            oldCts.Cancel();
            oldCts.Dispose();

            var ct = newCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(FlushDelayMs, ct);
                    onFlush();
                }
                catch (OperationCanceledException) { }
            }, ct);
        }
    }
}
