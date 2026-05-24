using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vkadmin_msg.Models;
using VkNet;
using VkNet.Model;

namespace vkadmin_msg
{
    public enum VkButtonColor { Default, Primary, Positive, Negative, Secondary }

    // ──────────────────────────────────────────────────────────────────────────
    //  VK Keyboard builder
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fluent builder for VK Bot keyboards (JSON format required by VK API).
    /// Usage:
    ///   var kb = new VkKeyboardBuilder()
    ///       .AddButton("Привет", "hello", VkButtonColor.Positive)
    ///       .NewRow()
    ///       .AddButton("О нас",  "about", VkButtonColor.Primary)
    ///       .Build();
    /// </summary>
    public class VkKeyboardBuilder
    {
        private readonly List<List<object>> _rows = new() { new List<object>() };
        private bool _oneTime = true;
        private bool _inline  = false;

        public VkKeyboardBuilder OneTime(bool value = true) { _oneTime = value; return this; }
        public VkKeyboardBuilder Inline(bool value  = true) { _inline  = value; return this; }

        /// <summary>Adds a text button to the current row.</summary>
        public VkKeyboardBuilder AddButton(string label, string payload, VkButtonColor color = VkButtonColor.Default)
        {
            // VK API requires payload to be a JSON-encoded string, e.g. "{\"button\":\"start\"}"
            // Passing a plain string like "start" causes: "button has invalid payload"
            var payloadJson = JsonSerializer.Serialize(new { button = payload });

            _rows.Last().Add(new
            {
                action = new { type = "text", label, payload = payloadJson },
                color  = color.ToString().ToLower()
            });
            return this;
        }

        public VkKeyboardBuilder AddLinkButton(string label, string url)
        {
            _rows.Last().Add(new
            {
                action = new { type = "open_link", label, link = url }
            });
            return this;
        }

        /// <summary>Starts a new row of buttons.</summary>
        public VkKeyboardBuilder NewRow()
        {
            _rows.Add(new List<object>());
            return this;
        }

        /// <summary>Serialises the keyboard to the JSON string expected by VK API.</summary>
        public string Build()
        {
            return JsonSerializer.Serialize(new
            {
                one_time = _oneTime,
                inline   = _inline,
                buttons  = _rows
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Button handler context
    // ──────────────────────────────────────────────────────────────────────────

    public class VkButtonContext
    {
        public required VkNet.Model.Message Message   { get; init; }
        public required string              Payload   { get; init; }
        public required long                PeerId    { get; init; }
        public required VkApi               Api       { get; init; }
        public          long?               GroupId   { get; init; }
        public          string              FirstName { get; init; } = "";
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  VkBot
    // ──────────────────────────────────────────────────────────────────────────

    internal class VkBot
    {
        private readonly ILogger<VkBot>  _logger;
        private readonly BotOptions      _options;
        private readonly VkApi           _api;
        private readonly MultiDataMap    _dataMap;

        // ── Responses & keyboards ─────────────────────────────────────────────

        public class VkResponse
        {
            public string        Text  { get; set; } = "";
            public List<string>? Photo { get; set; }
            public List<string>? Video { get; set; }
        }

        /// <summary>Texts loaded from vk_responses.json. Key = payload, Value = response.</summary>
        private Dictionary<string, VkResponse> _responses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keyboard JSON strings built from vk_keyboards.json. Key = payload.</summary>
        private Dictionary<string, string> _keyboards = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registered button handlers. Key = payload.</summary>
        private readonly Dictionary<string, Func<VkButtonContext, Task>> _buttonHandlers
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether VK button support is enabled (from vk_keyboards.json).</summary>
        private bool _buttonsEnabled = false;

        // ── Inactivity timers ─────────────────────────────────────────────────

        /// <summary>
        /// Per-user inactivity timers. Started after a button press; cancelled when
        /// the user sends a plain text message or presses another button.
        /// Key = peerId.
        /// </summary>
        private readonly Dictionary<long, Timer> _inactivityTimers = new();
        private readonly object _timerLock = new();

        // ── JSON models for vk_keyboards.json ─────────────────────────────────

        private class KeyboardConfig
        {
            public bool              Enabled { get; set; } = false;
            public List<MenuConfig>  Menus   { get; set; } = new();
        }

        private class MenuConfig
        {
            public string                  Payload { get; set; } = "";
            public bool                    OneTime { get; set; } = true;
            public List<List<ButtonConfig>> Rows   { get; set; } = new();
        }

        private class ButtonConfig
        {
            public string  Label    { get; set; } = "";
            public string? Payload  { get; set; }
            public string? Url      { get; set; }
            public string  Color    { get; set; } = "default";
        }

        // ──────────────────────────────────────────────────────────────────────

        public VkBot(ILogger<VkBot> logger, IOptions<BotOptions> options, VkApi api, MultiDataMap dataMap)
        {
            _logger = logger;
            _options = options.Value;
            _api = api;
            _dataMap = dataMap;

            LoadResponses();
            LoadKeyboards();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Public entry point — called from VkService for every incoming message
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles an incoming VK message: resolves the button payload (if any),
        /// invokes the registered handler, and returns whether the message was
        /// consumed by the bot (true) or should be forwarded to Telegram (false).
        /// </summary>
        public async Task<bool> HandleMessageAsync(VkNet.Model.Message message, string firstName)
        {
            var peerId = (long)message.PeerId!;
            var text   = message.Text ?? string.Empty;

            // ── 1. Resolve payload ────────────────────────────────────────────
            var payload = _buttonsEnabled
                ? (ExtractPayload(message.Payload)
                   ?? (text.Equals("Начать", StringComparison.OrdinalIgnoreCase) ? "start" : string.Empty))
                : string.Empty;

            bool isButton = !string.IsNullOrEmpty(payload) && _buttonHandlers.ContainsKey(payload);

            if (!isButton)
                return false; // nothing for the bot — let VkService forward the message

            // ── 2. Resolve first name once ────────────────────────────────────
            if (string.IsNullOrEmpty(firstName))
            {
                var vkUserInfo = _dataMap.GetVkUserInfo(peerId);
                firstName = vkUserInfo?.firstName ?? "";
            }

            // ── 3. Invoke handler ─────────────────────────────────────────────
            try
            {
                await _buttonHandlers[payload](new VkButtonContext
                {
                    Message   = message,
                    Payload   = payload,
                    PeerId    = peerId,
                    Api       = _api,
                    GroupId   = _options.Vk.VkGroupId,
                    FirstName = firstName,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VkBot] Ошибка в обработчике кнопки payload={Payload}", payload);
            }

            // ── 4. Start inactivity timer (button press → waiting for user reply) ─
            ResetInactivityTimer(peerId);

            return true; // message was handled by the bot
        }

        /// <summary>
        /// Should be called from VkService when the user sends a plain (non-button)
        /// message, so the inactivity timer is cancelled.
        /// </summary>
        public void StopInactivityTimer(long peerId)
        {
            lock (_timerLock)
            {
                if (_inactivityTimers.TryGetValue(peerId, out var existing))
                {
                    existing.Dispose();
                    _inactivityTimers.Remove(peerId);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Send helper (public — VkService may also call it directly)
        // ──────────────────────────────────────────────────────────────────────

        public async Task SendMessageAsync(
            long peerId,
            string text,
            string? keyboardJson = null,
            List<string>? photos = null,
            List<string>? videos = null)
        {

            var p = new MessagesSendParams
            {
                PeerId = peerId,
                RandomId = Random.Shared.Next(),
                Message = text,
                GroupId = (ulong)_options.Vk.VkGroupId,
            };

            if (!string.IsNullOrEmpty(keyboardJson))
                p.Keyboard = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<VkNet.Model.MessageKeyboard>(keyboardJson);

            var attachments = new List<MediaAttachment>();

            if (photos != null)
            {
                foreach (var photo in photos)
                {
                    var parts = photo.Replace("photo", "").Split('_');
                    attachments.Add(new VkNet.Model.Photo
                    {
                        OwnerId = long.Parse(parts[0]),
                        Id = long.Parse(parts[1])
                    });
                }
            }

            if (videos != null)
            {
                foreach (var video in videos)
                {
                    var parts = video.Replace("video", "").Split('_');
                    attachments.Add(new VkNet.Model.Video
                    {
                        OwnerId = long.Parse(parts[0]),
                        Id = long.Parse(parts[1])
                    });
                }
            }

            if (attachments.Count > 0) p.Attachments = attachments;

            try
            {
                await _api.Messages.SendAsync(p);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VkBot] Ошибка при отправке сообщения peerId={PeerId}", peerId);
            }

        }

        // ──────────────────────────────────────────────────────────────────────
        //  Inactivity timer
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resets (or starts) a 30-minute inactivity timer for <paramref name="peerId"/>.
        /// If the timer fires, sends the "inactivity" response and cleans up.
        /// Call after every button press. Call <see cref="StopInactivityTimer"/> when
        /// the user writes a plain message so the timer is cancelled.
        /// </summary>
        private void ResetInactivityTimer(long peerId)
        {
            lock (_timerLock)
            {
                if (_inactivityTimers.TryGetValue(peerId, out var existingTimer))
                {
                    // Просто сбрасываем время существующего таймера на 30 минут
                    existingTimer.Change(TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan);
                }
                else
                {
                    // Создаем новый таймер, если его не было
                    var timer = new Timer(async _ => await OnInactivityTimeoutAsync(peerId), null, TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan);
                    _inactivityTimers[peerId] = timer;
                }
            }
        }

        private async Task OnInactivityTimeoutAsync(long peerId)
        {
            // Удаляем таймер из словаря и утилизируем его
            lock (_timerLock)
            {
                if (_inactivityTimers.Remove(peerId, out var timer))
                {
                    timer.Dispose();
                }
            }

            try
            {
                var resp = GetResponse("inactivity");
                await SendMessageAsync(peerId, resp.Text);
                _logger.LogInformation("[VkBot] Inactivity message sent to peerId={PeerId}", peerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VkBot] Ошибка при отправке inactivity для peerId={PeerId}", peerId);
            }
        }


        // ──────────────────────────────────────────────────────────────────────
        //  Responses loader
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads vk_responses.json from the same directory as the executable.
        /// Format: { "payload_key": { "Text": "...", "Photo": [...], "Video": [...] }, ... }
        /// </summary>
        private void LoadResponses()
        {

            var configPath = _options.Vk.VkBotConfig.VkResponsesPath;
            var path = Path.IsPathRooted(configPath) ? configPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configPath);

            if (!File.Exists(path))
            {
                _logger.LogWarning("[VkBot] vk_responses.json не найден");
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                _responses = JsonSerializer.Deserialize<Dictionary<string, VkResponse>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

                _logger.LogInformation("[VkBot] Загружено {Count} ответов из vk_responses.json", _responses.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VkBot] Ошибка при загрузке vk_responses.json");
            }
        }

        /// <summary>
        /// Returns the response for the given payload key, substituting template
        /// placeholders ({firstName}). Always returns a copy — never mutates the cache.
        /// </summary>
        private VkResponse GetResponse(string payload, string? firstName = null)
        {
            var source = _responses.TryGetValue(payload, out var r)
                ? r
                : new VkResponse { Text = $"[Ответ для «{payload}» не найден]" };

            var resp = new VkResponse
            {
                Text  = source.Text,
                Photo = source.Photo,
                Video = source.Video,
            };

            if (!string.IsNullOrEmpty(firstName))
                resp.Text = resp.Text.Replace("{firstName}", firstName);

            return resp;
        }

        /// <summary>
        /// Returns true if <paramref name="text"/> exactly matches any bot response text.
        /// Used by VkService to silently drop MessageReply updates sent by the bot itself.
        /// </summary>
        public bool IsBotResponse(string text)
            => _responses.Values.Any(v => v.Text == text);

        // ──────────────────────────────────────────────────────────────────────
        //  Keyboards loader
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads vk_keyboards.json, builds keyboard JSON strings and registers
        /// a handler for every menu payload. If enabled=false, clears all handlers.
        /// </summary>
        private void LoadKeyboards()
        {

            var configPath = _options.Vk.VkBotConfig.VkKeyboardsPath;
            var path = Path.IsPathRooted(configPath) ? configPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configPath);

            if (!File.Exists(path))
            {
                _logger.LogWarning("[VkBot] vk_keyboards.json не найден — кнопки отключены.");
                _buttonsEnabled = false;
                return;
            }

            try
            {
                var json   = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<KeyboardConfig>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (config == null || !config.Enabled)
                {
                    _logger.LogInformation("[VkBot] Кнопки отключены (enabled: false).");
                    _buttonsEnabled = false;
                    _buttonHandlers.Clear();
                    return;
                }

                _buttonsEnabled = true;
                _buttonHandlers.Clear();
                _keyboards.Clear();

                foreach (var menu in config.Menus)
                {
                    var builder = new VkKeyboardBuilder().OneTime(menu.OneTime);

                    for (int i = 0; i < menu.Rows.Count; i++)
                    {
                        if (i > 0) builder.NewRow();
                        foreach (var btn in menu.Rows[i])
                        {
                            if (!string.IsNullOrEmpty(btn.Url))
                                builder.AddLinkButton(btn.Label, btn.Url);
                            else if (!string.IsNullOrEmpty(btn.Payload))
                            {
                                var color = btn.Color.ToLower() switch
                                {
                                    "primary"  => VkButtonColor.Primary,
                                    "positive" => VkButtonColor.Positive,
                                    "negative" => VkButtonColor.Negative,
                                    _          => VkButtonColor.Default
                                };
                                builder.AddButton(btn.Label, btn.Payload, color);
                            }
                        }
                    }

                    var keyboardJson = builder.Build();
                    var payloadKey   = menu.Payload; // capture for closure

                    _keyboards[payloadKey] = keyboardJson;

                    _buttonHandlers[payloadKey] = async ctx =>
                    {
                        var kb   = _keyboards.TryGetValue(payloadKey, out var k) ? k : null;
                        var resp = GetResponse(payloadKey, payloadKey == "start" ? ctx.FirstName : null);
                        await SendMessageAsync(ctx.PeerId, resp.Text, kb, photos: resp.Photo, videos: resp.Video);
                    };
                }

                _logger.LogInformation("[VkBot] Загружено {Count} меню из vk_keyboards.json.", config.Menus.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VkBot] Ошибка при загрузке vk_keyboards.json");
                _buttonsEnabled = false;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Payload helper
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// VK sends payload exactly as serialised in AddButton: {"button":"start"}
        /// Extracts the value of the "button" key (or other known keys).
        /// Returns the normalised lowercase payload string, or null if none.
        /// </summary>
        private static string? ExtractPayload(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                using var doc  = JsonDocument.Parse(raw.Trim());
                var       root = doc.RootElement;

                foreach (var key in new[] { "button", "command", "payload", "action" })
                    if (root.TryGetProperty(key, out var val))
                        return val.GetString()?.ToLower();

                return null;
            }
            catch { return null; }
        }
    }
}
