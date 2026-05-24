using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vkadmin_msg.Models;
using VkNet;
using VkNet.Exception;
using VkNet.Model;

namespace vkadmin_msg.Services
{
    internal class VkService : BackgroundService
    {
        private readonly ILogger<VkService> _logger;
        private readonly BotOptions _options;
        private readonly VkApi _api;
        private readonly MessageBridge _bridge;
        private readonly MultiDataMap _dataMap;
        private readonly AttachmentConverter _converter;
        private readonly VkBot? _vkBot;

        public VkService(
            ILogger<VkService> logger,
            IOptions<BotOptions> options,
            VkApi api,
            MessageBridge bridge,
            MultiDataMap dataMap,
            AttachmentConverter converter, VkBot vkBot)
        {
            _logger    = logger;
            _options   = options.Value;
            _api       = api;
            _bridge    = bridge;
            _dataMap   = dataMap;
            _converter = converter;

            if (_options.Vk.AllowVkBot)
                _vkBot = vkBot;
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            try
            {
                await _api.AuthorizeAsync(new ApiAuthParams
                {
                    AccessToken = _options.Vk.VkGroupToken
                });
                _logger.LogInformation("Успешная авторизация во ВКонтакте.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Не удалось авторизоваться в VK API!");
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await InitializeAsync(ct);
            _logger.LogInformation("VkService запущен.");

            _ = ProcessOutgoingVkQueueAsync(ct);

            var longPollServer = _api.Groups.GetLongPollServer(Convert.ToUInt64(_options.Vk.VkGroupId));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var history = _api.Groups.GetBotsLongPollHistory(new BotsLongPollHistoryParams
                    {
                        Server = longPollServer.Server,
                        Ts     = longPollServer.Ts,
                        Key    = longPollServer.Key,
                        Wait   = 10
                    });

                    longPollServer.Ts = history.Ts;

                    if (history.Updates == null) continue;

                    foreach (var update in history.Updates)
                    {
                        if (update.Type.Value == VkNet.Enums.StringEnums.GroupUpdateType.MessageNew)
                        {

                            var msg = (update.Instance as MessageNew)?.Message;
                            if (msg != null) await HandleNewMessage(msg, ct);

                        }
                        else if (update.Type.Value == VkNet.Enums.StringEnums.GroupUpdateType.MessageEdit)
                        {

                            var msg = update.Instance as VkNet.Model.Message;
                            if (msg == null) continue;

                            bool isReply = _options.TelegramBot.AllowReply && msg.AdminAuthorId != null ? true : false;

                            if (msg.AdminAuthorId == null && msg.FromId < 0) continue;
                            if (!_options.TelegramBot.AllowReply && msg.FromId < 0) continue;

                            HandleEditMessage(msg, ct, isReply);

                        }
                        else if (update.Type.Value == VkNet.Enums.StringEnums.GroupUpdateType.MessageReply)
                        {

                            if (!_options.TelegramBot.AllowReply) continue;

                            var msg = update.Instance as VkNet.Model.Message;
                            if (msg == null) continue;

                            if (msg.AdminAuthorId == null && msg.FromId < 0) continue;

                            await HandleNewMessage(msg, ct, true);

                        }
                    }
                }
                catch (VkNet.Exception.LongPollKeyExpiredException)
                {
                    _logger.LogDebug("[VK LongPoll] Ключ истёк, обновляем...");
                    try
                    {
                        var fresh = _api.Groups.GetLongPollServer(Convert.ToUInt64(_options.Vk.VkGroupId));
                        longPollServer.Key = fresh.Key;
                        longPollServer.Ts = fresh.Ts;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation($"[VK LongPoll] Не удалось обновить ключ: {ex.Message}");
                        await SafeDelay(10000, ct);
                    }
                }
                catch (VkNet.Exception.LongPollInfoLostException)
                {
                    _logger.LogDebug("[VK LongPoll] История потеряна, переподключаемся...");
                    try
                    {
                        longPollServer = _api.Groups.GetLongPollServer(Convert.ToUInt64(_options.Vk.VkGroupId));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation($"[VK LongPoll] Не удалось переподключиться: {ex.Message}");
                        await SafeDelay(10000, ct);
                    }
                }
                catch (VkNet.Exception.LongPollOutdateException)
                {
                    _logger.LogDebug("[VK LongPoll] Ts устарел, обновляем сервер...");
                    try
                    {
                        longPollServer = _api.Groups.GetLongPollServer(Convert.ToUInt64(_options.Vk.VkGroupId));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation($"[VK LongPoll] Не удалось обновить сервер: {ex.Message}");
                        await SafeDelay(10000, ct);
                    }
                }
                catch (OperationCanceledException ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        _logger.LogInformation("[VK LongPoll] Получена отмена, выходим...");
                        break;
                    }
                    _logger.LogInformation("[VK LongPoll] OperationCanceledException от VkNet (таймаут запроса), продолжаем. {Msg}", ex.Message);
                }
                catch (Exception ex)
                {
                    // Log full type so we can identify unknown exceptions (e.g. SocketException,
                    // HttpRequestException) that VkNet does not wrap in its own types
                    _logger.LogError($"[VK LongPoll Error] {ex.GetType().Name}: {ex.Message}");
                    await SafeDelay(5000, ct);
                }
            }

        }

        private static async Task SafeDelay(int milliseconds, CancellationToken ct)
        {
            try { await Task.Delay(milliseconds, ct); }
            catch (OperationCanceledException) { }
        }

        private void HandleEditMessage(VkNet.Model.Message message, CancellationToken ct, bool reply = false) 
        {

            if (message.FromId == null) return;

            var (tgThreadId, _) = _dataMap.GetByPeerId(message.PeerId);

            if (tgThreadId != 0)
            {
                _bridge.SendEditToTg(message.PeerId, message, reply);
            }

        }

        private async Task HandleNewMessage(VkNet.Model.Message message, CancellationToken ct, bool reply = false) 
        {

            if (message.FromId == null) return;

            if (message.Attachments.Count > 0 || message.ForwardedMessages.Count > 0)
            {

                var realMessage = await RequestMessage(message.Id, ct);

                if (realMessage.Attachments.Count > message.Attachments.Count)
                    message.Attachments = realMessage.Attachments;
                if (realMessage.ForwardedMessages.Count > message.ForwardedMessages.Count)
                    message.ForwardedMessages = realMessage.ForwardedMessages;
            }

            var (tgThreadId, _) = _dataMap.GetByPeerId(message.PeerId);


            if (tgThreadId != 0)
            {
                _bridge.SendMessageToTg(message.PeerId, message, reply);
                if (_vkBot != null) _vkBot.StopInactivityTimer(message.PeerId!.Value);
            }
            else
            {
                var vkUser = await _api.Users.GetAsync(new[] { message.PeerId!.Value });
                string firstName = vkUser.FirstOrDefault()?.FirstName ?? "";
                string lastName = vkUser.FirstOrDefault()?.LastName ?? "";

                _bridge.SendFirstMessageWithTopicToTg(message.PeerId, message, firstName, lastName);

                if ((message.Text.Equals("Начать", StringComparison.OrdinalIgnoreCase) || message.Payload != null) && _vkBot != null)
                {
                    await _vkBot.HandleMessageAsync(message, firstName);
                    return;
                }
            }

            if ((message.Text.Equals("Начать", StringComparison.OrdinalIgnoreCase) || message.Payload != null) && _vkBot != null)
                await _vkBot.HandleMessageAsync(message, "");
        }


        // ── Исходящие сообщения TG → VK ──────────────────────────────────────

        private async Task ProcessOutgoingVkQueueAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var task in _bridge.VkReader.ReadAllAsync(ct))
                {
                    try
                    {
                        switch (task.VkActionType)
                        {
                            case VkActionType.NewMessage:
                                await SendToVkAsync(task, ct);
                                break;
                            case VkActionType.EditMessage:
                                await EditToVkAsync(task, ct);
                                break;
                            case VkActionType.MarkAsRead:
                                bool success = await MarkAsReadVkAsync(task, ct);
                                task.ResultSource?.TrySetResult(success);
                                break;
                            default:
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        var (peerId, _) = _dataMap.GetByThreadId(task.TgThreadId);
                        var userInfo = _dataMap.GetVkUserInfo(peerId);
                        _logger.LogError(ex, $"[VK] Ошибка при отправке сообщения в VK: {userInfo?.firstName} {userInfo?.lastName} | {task.VkActionType}");
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogInformation("[VK] Воркер очередей VK остановлен. {Ex}", ex.Message);
            }
        }

        private async Task<bool> MarkAsReadVkAsync(ToVkMessageTask task, CancellationToken ct)
        {
            var (vkPeerId, _) = _dataMap.GetByThreadId(task.TgThreadId);
            if (vkPeerId == null || vkPeerId == 0) return false;

            try
            {
                await _api.Messages.MarkAsReadAsync(vkPeerId.ToString(),
                    groupId: _options.Vk.VkGroupId, token: ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[VK] Ошибка при нажатии кнопки \"отметить как прочитанный\": {ex.Message}");
                return false;
            }
        }

        private async Task EditToVkAsync(ToVkMessageTask task, CancellationToken ct)
        {
            var tgMsg = task.Messages.FirstOrDefault();
            var (vkPeerId, messages) = _dataMap.GetByThreadId(task.TgThreadId);

            if (tgMsg == null) return;
            if (vkPeerId == null || vkPeerId == 0)
            {
                _logger.LogWarning("[VK] Не найден VK peer для tgThreadId={ThreadId}", task.TgThreadId);
                return;
            }

            // Находим VK message id по TG message id
            var (vkMessageId, _) = messages.FirstOrDefault(m => m.TgMsgId == tgMsg.MessageId);
            if (vkMessageId == 0)
            {
                _logger.LogWarning("[VK] Не найден VK messageId для tgMessageId={MessageId}", tgMsg.MessageId);
                return;
            }

            var text = tgMsg.Text ?? tgMsg.Caption ?? string.Empty;
            var attachments = new List<VkNet.Model.MediaAttachment>();

            attachments = await _converter.UploadFromTelegramAsync(tgMsg, vkPeerId.Value, ct);

            try
            {
                await _api.Messages.EditAsync(new MessageEditParams
                {
                    PeerId = vkPeerId.Value,
                    MessageId = vkMessageId,
                    Message = text,
                    Attachments = attachments.Count > 0 ? attachments : null,
                    GroupId = (ulong)_options.Vk.VkGroupId
                });
            }
            catch (Exception ex) when (ex is CannotSendToUserFirstlyException || ex is CannotSendDuePrivacyException)
            {
                _logger.LogError("[VK] Пользователь {PeerId} запретил вам писать личные сообщения", vkPeerId);
                _bridge.SendMessageToTg(vkPeerId, new VkNet.Model.Message { Text = "⚠️ [ERR] Пользователь запретил вам писать личные сообщения" });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[VK] Ошибка при редактировании в VK: {ex.Message}");
            }

            _logger.LogDebug(
                "Сообщение отредактировано в VK (peerId={PeerId}, vkMsgId={VkMsgId})",
                vkPeerId, vkMessageId);
        }

        private async Task SendToVkAsync(ToVkMessageTask task, CancellationToken ct)
        {
            var tgMsg = task.Messages.FirstOrDefault();

            // Определяем получателя по tgThreadId → vkPeerId
            var (vkPeerId, messages) = _dataMap.GetByThreadId(task.TgThreadId);

            if (vkPeerId == null || vkPeerId == 0 || tgMsg == null) return;

            var text = tgMsg.Text ?? tgMsg.Caption ?? string.Empty;

            long? replyMsg = 0;
            if (tgMsg.ReplyToMessage?.MessageId != tgMsg.MessageThreadId) 
            {
                var (foundVkMsgId, _) = messages.FirstOrDefault(
                    repl => repl.TgMsgId == tgMsg.ReplyToMessage?.MessageId);

                if (foundVkMsgId != null && foundVkMsgId != 0) 
                    replyMsg = foundVkMsgId;
            }

            var attachments = new List<VkNet.Model.MediaAttachment>();

            foreach (var msg in task.Messages)
            {
                var uploaded = await _converter.UploadFromTelegramAsync(msg, vkPeerId.Value, ct);
                attachments.AddRange(uploaded);

                if (task.Messages.Count > 1)
                    await Task.Delay(300, ct);
            }

            try
            {
                var sentMsgId = await _api.Messages.SendAsync(new MessagesSendParams
                {
                    PeerId = vkPeerId,
                    Message = text,
                    Attachments = attachments,
                    ReplyTo = replyMsg,
                    GroupId = (ulong)_options.Vk.VkGroupId,
                    RandomId = Random.Shared.Next()
                });

                foreach (var msg in task.Messages)
                {
                    _dataMap.Add(vkPeerId, task.TgThreadId,
                        vkMessageId: sentMsgId,
                        tgMessageId: msg.MessageId);
                }

                _logger.LogDebug(
                "Сообщение отправлено в VK (peerId={PeerId}, vkMsgId={VkMsgId})",
                vkPeerId, sentMsgId);
            }
            catch (Exception ex) when (ex is CannotSendToUserFirstlyException || ex is CannotSendDuePrivacyException)
            {
                _logger.LogError("[VK] Пользователь {PeerId} запретил вам писать личные сообщения", vkPeerId);
                _bridge.SendMessageToTg(vkPeerId, new VkNet.Model.Message { Text = "⚠️ [ERR] Пользователь запретил вам писать личные сообщения" });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[VK] Ошибка при отправке в VK: {ex.Message}");
            }

        }

        // ── Вспомогательное ──────────────────────────────────────────────────

        private async Task<VkNet.Model.Message> RequestMessage(long? msgId, CancellationToken ct)
        {
            var message = new VkNet.Model.Message();

            if (msgId == null || msgId <= 0) return message;

            var messageCollection = await _api.Messages.GetByIdAsync(
                new[] { (ulong)msgId.Value }, null,
                groupId: (ulong)_options.Vk.VkGroupId, token: ct);

            if (messageCollection == null || messageCollection.Count == 0)
                return message;

            return messageCollection.FirstOrDefault() ?? message;
        }
    }
}
