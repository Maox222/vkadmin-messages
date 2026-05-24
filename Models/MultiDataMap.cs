using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace vkadmin_msg.Models
{
    public class MultiDataMap
    {
        private readonly ILogger<MultiDataMap> _logger;
        private readonly object _lock = new();
        private readonly string _filePath;

        public record VkUserInfo(long? vkPeerId, string? firstName, string? lastName);

        // Основные индексы
        private readonly Dictionary<VkUserInfo, HashSet<(long? VkMsgId, int TgMsgId)>> _byPeer   = new();
        private readonly Dictionary<int,  HashSet<(long? VkMsgId, int TgMsgId)>> _byThread = new();

        // Обратные индексы для O(1)-поиска ключа по ссылке на set
        // (раньше использовался FirstOrDefault + ReferenceEquals — O(n))
        private readonly Dictionary<long?, int>  _peerToThread = new();
        private readonly Dictionary<int,  long?> _threadToPeer = new();

        private readonly Channel<bool> _saveChannel = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        public MultiDataMap(ILogger<MultiDataMap> logger, IOptions<BotOptions> options)
        {
            _logger = logger;

            var configPath = options.Value.DataMapFilePath;
            _filePath = Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configPath);

            LoadMap();
            _ = Task.Run(ProcessSaveQueueAsync);
        }

        // ── Запись ────────────────────────────────────────────────────────────

        public void Add(long? vkPeerId, int tgThreadId, long? vkMessageId, int tgMessageId, string? firstName = null, string? lastName = null)
        {
            lock (_lock)
            {
                VkUserInfo vkUserInfo = _byPeer.FirstOrDefault(vk => vk.Key.vkPeerId == vkPeerId).Key ?? new VkUserInfo(vkPeerId, firstName, lastName);
                if (!_byPeer.TryGetValue(vkUserInfo, out var set))
                {
                    set = new HashSet<(long?, int)>();
                    _byPeer[vkUserInfo]    = set;
                    _byThread[tgThreadId] = set;
                    _peerToThread[vkPeerId]  = tgThreadId;
                    _threadToPeer[tgThreadId] = vkPeerId;
                }
                set.Add((vkMessageId, tgMessageId));
            }

            _saveChannel.Writer.TryWrite(true);
        }

        public VkUserInfo? GetVkUserInfo(long? vkPeerId) 
        {
            lock (_lock)
            {
                return _byPeer.Keys.FirstOrDefault(vk => vk.vkPeerId == vkPeerId);
            }
        }
        /// <summary>
        /// Удаляет диалог по vkPeerId из обоих индексов и обратных словарей.
        /// Используется, например, при закрытии топика.
        /// </summary>
        public bool RemoveByPeerId(long? vkPeerId)
        {
            bool removed;

            lock (_lock)
            {
                var vkUser = _byPeer.FirstOrDefault(vk => vk.Key.vkPeerId == vkPeerId).Key;
                removed = _byPeer.Remove(vkUser);
                if (removed && _peerToThread.TryGetValue(vkPeerId, out var tgThreadId))
                {
                    _byThread.Remove(tgThreadId);
                    _peerToThread.Remove(vkPeerId);
                    _threadToPeer.Remove(tgThreadId);
                }
            }

            if (removed)
                _saveChannel.Writer.TryWrite(true);

            return removed;
        }

        /// <summary>
        /// Удаляет диалог по tgThreadId.
        /// </summary>
        /// 
        public bool RemoveByThreadId(int tgThreadId)
        {
            bool removed;
            lock (_lock)
            {
                removed = _byThread.Remove(tgThreadId);
                if (removed && _threadToPeer.TryGetValue(tgThreadId, out var vkPeerId))
                {
                    _byPeer.Remove(_byPeer.FirstOrDefault(vk => vk.Key.vkPeerId == vkPeerId).Key);
                    _peerToThread.Remove(vkPeerId);
                    _threadToPeer.Remove(tgThreadId);
                }
            }

            if (removed)
                _saveChannel.Writer.TryWrite(true);

            return removed;
        }

        /// <summary>
        /// Удаляет пары по tgThreadId.
        /// </summary>
        /// 
        public void RemoveMessageIdPairs(long? vkPeerId, long? vkMessageId, int tgMessageId)
        {
            lock (_lock)
            {
                var vkUserInfo = _byPeer.FirstOrDefault(vk => vk.Key.vkPeerId == vkPeerId).Key;
                if (vkUserInfo == null) return;

                if (_byPeer.TryGetValue(vkUserInfo, out var set))
                    set.Remove((vkMessageId, tgMessageId));
            }

            _saveChannel.Writer.TryWrite(true);
        }

        // ── Чтение ────────────────────────────────────────────────────────────

        public (int TgThreadId, List<(long? VkMsgId, int TgMsgId)> Messages) GetByPeerId(long? vkPeerId)
        {
            lock (_lock)
            {
                var vkUserInfo = _byPeer.FirstOrDefault(vk => vk.Key.vkPeerId == vkPeerId).Key;

                if (vkUserInfo == null || !_byPeer.TryGetValue(vkUserInfo, out var set))
                    return (0, []);

                // O(1) благодаря обратному индексу
                _peerToThread.TryGetValue(vkPeerId, out var tgThreadId);
                return (tgThreadId, set.ToList());
            }
        }

        public (long? VkPeerId, List<(long? VkMsgId, int TgMsgId)> Messages) GetByThreadId(int tgThreadId)
        {
            lock (_lock)
            {
                if (!_byThread.TryGetValue(tgThreadId, out var set))
                    return (0, []);

                _threadToPeer.TryGetValue(tgThreadId, out var vkPeerId);
                return (vkPeerId, set.ToList());
            }
        }

        // ── Персистентность ───────────────────────────────────────────────────

        private async Task ProcessSaveQueueAsync()
        {
            while (await _saveChannel.Reader.WaitToReadAsync())
            {
                if (_saveChannel.Reader.TryRead(out _))
                {
                    await Task.Delay(1000);
                    while (_saveChannel.Reader.TryRead(out _)) { } // сбрасываем накопившиеся
                    await SaveMapToDiskAsync();
                }
            }
        }

        private async Task SaveMapToDiskAsync()
        {
            try
            {
                string? jsonText = null;
                lock (_lock)
                {
                    var dto = _byPeer.Select(kvp => new JsonRow(
                        VkUser:   kvp.Key,
                        TgThreadId: _peerToThread.TryGetValue(kvp.Key.vkPeerId, out var tid) ? tid : 0,
                        Messages:   kvp.Value.Select(m => new JsonMessage(m.VkMsgId, m.TgMsgId)).ToList()
                    )).ToList();

                    if (dto.Count > 0)
                        jsonText = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
                }

                if (jsonText != null)
                    await File.WriteAllTextAsync(_filePath, jsonText);
                _logger.LogDebug("DataMap сохранён на диск.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при сохранении DataMap на диск.");
            }
        }

        private void LoadMap()
        {
            try
            {
                if (!File.Exists(_filePath)) return;

                var json = File.ReadAllText(_filePath);
                var dto  = JsonSerializer.Deserialize<List<JsonRow>>(json);
                if (dto == null) return;

                lock (_lock)
                {
                    _byPeer.Clear();
                    _byThread.Clear();
                    _peerToThread.Clear();
                    _threadToPeer.Clear();

                    foreach (var row in dto)
                    {
                        var set = new HashSet<(long?, int)>();
                        foreach (var msg in row.Messages)
                            set.Add((msg.VkMsgId, msg.TgMsgId));

                        _byPeer[row.VkUser]      = set;
                        _byThread[row.TgThreadId]  = set;
                        // ── BUG FIX: раньше _map2 не заполнялся при загрузке,
                        // из-за чего после рестарта GetByPeerId возвращал tgThreadId = 0
                        _peerToThread[row.VkUser.vkPeerId]    = row.TgThreadId;
                        _threadToPeer[row.TgThreadId]  = row.VkUser.vkPeerId;
                    }
                }

                _logger.LogInformation("DataMap загружен: {Count} диалогов.", dto.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при загрузке DataMap с диска.");
            }
        }

        // ── DTO для сериализации ──────────────────────────────────────────────
        // BUG FIX: было int — теперь long, чтобы не обрезать VK ID > 2^31
        private record JsonMessage(long? VkMsgId, int TgMsgId);
        private record JsonRow(VkUserInfo VkUser, int TgThreadId, List<JsonMessage> Messages);
    }
}
