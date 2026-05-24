using System.Threading.Channels;

namespace vkadmin_msg
{
    public enum TgActionType
    {
        NewMessage,
        CreateTopicAndMessage,
        UserTyping,
        EditMessage
    }
    public enum VkActionType
    {
        NewMessage,
        EditMessage,
        MarkAsRead
    }

    // Из Telegram в VK
    public record ToVkMessageTask(VkActionType VkActionType, int TgThreadId, IReadOnlyList<Telegram.Bot.Types.Message> Messages, TaskCompletionSource<bool>? ResultSource = null);
    // Из VK в Telegram
    public record ToTgMessageTask(TgActionType TgActionType, long? VkPeerId, VkNet.Model.Message message, string? firstName = null, string? lastName = null, bool isReply = false);

    internal class MessageBridge
    {
        private readonly Channel<ToVkMessageTask> _toVkChannel = Channel.CreateUnbounded<ToVkMessageTask>(
            new UnboundedChannelOptions { SingleReader = true });

        private readonly Channel<ToTgMessageTask> _toTgChannel = Channel.CreateUnbounded<ToTgMessageTask>(
            new UnboundedChannelOptions { SingleReader = true });

        // ── Запись из TG → VK ─────────────────────────────────────────────────

        public void SendMessageToVk(int tgThreadId, IReadOnlyList<Telegram.Bot.Types.Message> Messages) =>
            _toVkChannel.Writer.TryWrite(new ToVkMessageTask(VkActionType.NewMessage, tgThreadId, Messages));

        public void SendEditToVk(int tgThreadId, IReadOnlyList<Telegram.Bot.Types.Message> Messages) =>
            _toVkChannel.Writer.TryWrite(new ToVkMessageTask(VkActionType.EditMessage, tgThreadId, Messages));
        public Task<bool> MarkAsReadToVk(int tgThreadId, IReadOnlyList<Telegram.Bot.Types.Message> Messages)
        {
            var tcs = new TaskCompletionSource<bool>();
            _toVkChannel.Writer.TryWrite(new ToVkMessageTask(VkActionType.MarkAsRead, tgThreadId, Messages, tcs));
            return tcs.Task;
        }


        // ── Запись из VK → TG ─────────────────────────────────────────────────

        public void SendMessageToTg(long? vkPeerId, VkNet.Model.Message message, bool replyMark = false) =>
            _toTgChannel.Writer.TryWrite(new ToTgMessageTask(TgActionType.NewMessage, vkPeerId, message, isReply: replyMark));

        public void SendFirstMessageWithTopicToTg(long? vkPeerId, VkNet.Model.Message message, string firstName, string lastName) =>
            _toTgChannel.Writer.TryWrite(new ToTgMessageTask(TgActionType.CreateTopicAndMessage, vkPeerId, message, firstName, lastName));

        public void SendEditToTg(long? vkPeerId, VkNet.Model.Message message, bool reply = false) =>
            _toTgChannel.Writer.TryWrite(new ToTgMessageTask(TgActionType.EditMessage, vkPeerId, message, isReply: reply));

        // ── Чтение ────────────────────────────────────────────────────────────

        public ChannelReader<ToVkMessageTask> VkReader => _toVkChannel.Reader;
        public ChannelReader<ToTgMessageTask> TgReader => _toTgChannel.Reader;
    }
}
