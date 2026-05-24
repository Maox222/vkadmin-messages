using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using vkadmin_msg.Models;
using VkNet;
using VkNet.Model;

namespace vkadmin_msg.Services
{
    public sealed class AttachmentGroup
    {
        public AttachmentGroupType Type { get; init; }

        /// <summary>PhotoVideo / Document / Audio → SendMediaGroup.</summary>
        public IReadOnlyList<IAlbumInputMedia>? AlbumMedia { get; init; }

        /// <summary>AudioMessage → SendVoice.</summary>
        public (InputFile File, int? Duration)? Voice { get; init; }

        /// <summary>
        /// Sticker → SendSticker.
        /// Содержит скачанный WEBP (или PNG если WEBP недоступен).
        /// </summary>
        public InputFile? StickerFile { get; init; }

        /// <summary>
        /// ExternalVideo — видео без прямой ссылки (YouTube, внешние хостинги).
        /// Содержит ссылку на плеер ВКонтакте для отправки текстом.
        /// </summary>
        public string? ExternalVideoUrl { get; init; }
    }

    public enum AttachmentGroupType
    {
        PhotoVideo,
        Document,
        Audio,
        AudioMessage,
        ExternalVideo,  // Отправляется отдельным текстовым сообщением со ссылкой
        Sticker         // Отправляется через SendSticker (WEBP) или SendPhoto (PNG-превью)
    }

    /// <summary>
    /// Конвертирует вложения VK → группы для Telegram (ConvertAsync)
    /// и загружает вложения из Telegram → VK (UploadFromTelegramAsync).
    /// Регистрируется через services.AddHttpClient&lt;AttachmentConverter&gt;().
    /// </summary>
    public sealed class AttachmentConverter
    {
        private readonly HttpClient _http;
        private readonly VkApi _vkApi;
        private readonly ILogger<AttachmentConverter> _logger;
        private readonly BotOptions _options;
        private ITelegramBotClient? _botClient;

        public AttachmentConverter(
            HttpClient http, VkApi vkApi,
            ILogger<AttachmentConverter> logger,
            IOptions<BotOptions> options, ITelegramBotClient botClient)
        {
            _http     = http;
            _vkApi    = vkApi;
            _logger   = logger;
            _botClient = botClient;
            _options = options.Value;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // VK → Telegram
        // ═══════════════════════════════════════════════════════════════════════

        public async Task<List<AttachmentGroup>> ConvertAsync(
            VkNet.Model.Message vkMessage,
            string? captionText,
            CancellationToken ct = default)
        {
            var photoVideo    = new List<IAlbumInputMedia>();
            var documents     = new List<IAlbumInputMedia>();
            var audios        = new List<IAlbumInputMedia>();
            var voices        = new List<(InputFile File, int? Duration)>();
            var externalLinks = new List<string>();
            var stickers      = new List<InputFile>();

            bool captionSet = false;

            foreach (var attachment in vkMessage.Attachments)
            {
                switch (attachment.Instance)
                {
                    // ── ФОТО ──────────────────────────────────────────────────
                    case VkNet.Model.Photo photo:
                    {
                        var url = GetBestPhotoUrl(photo);
                        if (url is null) break;

                        var media = new InputMediaPhoto(InputFile.FromUri(url));
                        TrySetCaption(media, captionText, ref captionSet);
                        photoVideo.Add(media);
                        break;
                    }

                    // ── ВИДЕО ─────────────────────────────────────────────────
                    case VkNet.Model.Video video:
                    {
                        var directUrl = await ResolveVideoUrlAsync(video, ct);

                        if (directUrl != null)
                        {
                            var input = await DownloadToStreamAsync(directUrl, $"video_{video.Id}.mp4", ct);
                            var media = new InputMediaVideo(input)
                            {
                                Width             = (int)video.Width,
                                Height            = (int)video.Height,
                                Duration          = (int)video.Duration,
                                SupportsStreaming = true
                            };
                            TrySetCaption(media, captionText, ref captionSet);
                            photoVideo.Add(media);
                        }
                        else
                        {
                            var fallbackUrl = video.Player?.ToString()
                                ?? $"https://vk.com/video{video.OwnerId}_{video.Id}";
                            externalLinks.Add(fallbackUrl);
                        }
                        break;
                    }

                    // ── ДОКУМЕНТ ──────────────────────────────────────────────
                    case VkNet.Model.Document doc:
                    {
                        if (doc.Uri is null) break;

                        var input = await DownloadToStreamAsync(
                            doc.Uri.ToString(), doc.Title ?? $"doc_{doc.Id}", ct);
                        var media = new InputMediaDocument(input);
                        TrySetCaption(media, captionText, ref captionSet);
                        documents.Add(media);
                        break;
                    }

                    // ── АУДИО (музыка) ────────────────────────────────────────
                    case VkNet.Model.Audio audio:
                    {
                            if (audio.Url is null) break;

                            try
                            {
                                var title = $"{audio.Artist} - {audio.Title}".Trim(' ', '-');
                                var url = audio.Url.ToString();

                                byte[] audioBytes;

                                if (url.Contains(".m3u8"))
                                {
                                    // HLS поток — конвертируем через ffmpeg
                                    audioBytes = await ConvertHlsToMp3Async(url, ct);
                                }
                                else
                                {
                                    audioBytes = await _http.GetByteArrayAsync(url, ct);
                                }

                                var input = InputFile.FromStream(new MemoryStream(audioBytes), $"{title}.mp3");
                                var media = new InputMediaAudio(input)
                                {
                                    Title = title,
                                    Performer = audio.Artist,
                                    Duration = (int)audio.Duration
                                };
                                TrySetCaption(media, captionText, ref captionSet);
                                audios.Add(media);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Ошибка получения аудио {Id}", audio.Id);
                            }
                            break;
                        }

                    // ── ГОЛОСОВОЕ СООБЩЕНИЕ ───────────────────────────────────
                    case VkNet.Model.AudioMessage audioMsg:
                    {
                        var url = audioMsg.LinkOgg?.ToString() ?? audioMsg.LinkMp3?.ToString();
                        if (url is null) break;

                        var input = await DownloadToStreamAsync(url, $"voice_{audioMsg.Id}.ogg", ct);
                        voices.Add((input, (int?)audioMsg.Duration));
                        break;
                    }

                    // ── СТИКЕР ────────────────────────────────────────────────
                    case VkNet.Model.Sticker sticker:
                    {
                        var url = GetBestStickerUrl(sticker);
                        if (url is null) break;

                        var ext      = url.Contains(".webp", StringComparison.OrdinalIgnoreCase) ? "webp" : "png";
                        var fileName = $"sticker_{sticker.Id}.{ext}";

                        var input = await DownloadToStreamAsync(url, fileName, ct);
                        stickers.Add(input);
                        break;
                    }
                }
            }

            var result = new List<AttachmentGroup>();

            if (photoVideo.Count > 0)
                result.Add(new AttachmentGroup { Type = AttachmentGroupType.PhotoVideo, AlbumMedia = photoVideo });
            if (documents.Count > 0)
                result.Add(new AttachmentGroup { Type = AttachmentGroupType.Document, AlbumMedia = documents });
            if (audios.Count > 0)
                result.Add(new AttachmentGroup { Type = AttachmentGroupType.Audio, AlbumMedia = audios });

            foreach (var v in voices)
                result.Add(new AttachmentGroup { Type = AttachmentGroupType.AudioMessage, Voice = v });
            foreach (var link in externalLinks)
                result.Add(new AttachmentGroup { Type = AttachmentGroupType.ExternalVideo, ExternalVideoUrl = link });
            foreach (var s in stickers)
                result.Add(new AttachmentGroup { Type = AttachmentGroupType.Sticker, StickerFile = s });

            return result;
        }

        private async Task<byte[]> ConvertHlsToMp3Async(string m3u8Url, CancellationToken ct)
        {
            var tempMp3 = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-i \"{m3u8Url}\" -vn -acodec libmp3lame -q:a 2 -y \"{tempMp3}\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi)!;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(timeoutCts.Token);
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogError("ffmpeg ошибка: {Err}", stderr);
                    throw new Exception($"ffmpeg завершился с ошибкой (code={process.ExitCode})");
                }

                return await File.ReadAllBytesAsync(tempMp3, ct);
            }
            finally
            {
                if (File.Exists(tempMp3)) File.Delete(tempMp3);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Telegram → VK  (Upload-методы)
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Собирает список вложений VK из сообщения Telegram.
        /// Скачивает файлы из TG, загружает в VK и возвращает готовые MediaAttachment.
        /// </summary>
        public async Task<List<MediaAttachment>> UploadFromTelegramAsync(
            Telegram.Bot.Types.Message tgMsg,
            long vkPeerId,
            CancellationToken ct = default)
        {
            var attachments = new List<MediaAttachment>();

            // ── Фото ──────────────────────────────────────────────────────────
            if (tgMsg.Photo is { Length: > 0 })
            {
                var bestPhoto = tgMsg.Photo.Last(); // наибольший размер
                var uploaded  = await UploadPhotoAsync(vkPeerId, bestPhoto.FileId, ct);
                if (uploaded != null) attachments.Add(uploaded);
            }

            // ── Документ (файл, GIF, анимация) ───────────────────────────────
            if (tgMsg.Document != null)
            {
                var uploaded = await UploadDocumentAsync(
                    vkPeerId, tgMsg.Document.FileId,
                    tgMsg.Document.FileName ?? "file", ct);
                if (uploaded != null) attachments.Add(uploaded);
            }

            // ── Стикер ────────────────────────────────────────────────────────
            // VK не принимает стикеры через upload — отправляем как фото (.webp / превью .png)
            if (tgMsg.Sticker != null)
            {
                if (tgMsg.Sticker.IsVideo)
                {
                    var (bytes, _) = await DownloadTgFileAsync(tgMsg.Sticker.FileId, "sticker.webm", ct);
                    var gifBytes = await ConvertWebmToGifAsync(bytes, ct);
                    if (gifBytes != null)
                    {
                        var uploaded = await UploadDocumentBytesAsync(vkPeerId, gifBytes, "sticker.gif", ct);
                        if (uploaded != null) attachments.Add(uploaded);
                    }
                    else
                    {
                        // Fallback — превью как фото
                        var fileId = tgMsg.Sticker.Thumbnail?.FileId ?? tgMsg.Sticker.FileId;
                        var uploaded = await UploadPhotoAsync(vkPeerId, fileId, ct);
                        if (uploaded != null) attachments.Add(uploaded);
                    }
                }
                else if (tgMsg.Sticker.IsAnimated)
                {
                    var fileId = tgMsg.Sticker.Thumbnail?.FileId ?? tgMsg.Sticker.FileId;
                    var uploaded = await UploadPhotoAsync(vkPeerId, fileId, ct);
                    if (uploaded != null) attachments.Add(uploaded);
                }
                else
                {
                    var uploaded = await UploadPhotoAsync(vkPeerId, tgMsg.Sticker.FileId, ct);
                    if (uploaded != null) attachments.Add(uploaded);
                }
            }

            // ── Голосовое сообщение ───────────────────────────────────────────
            //if (tgMsg.Voice != null)
            //{
            //    var uploaded = await UploadVoiceAsync(vkPeerId, tgMsg.Voice.FileId, ct);
            //    if (uploaded != null) attachments.Add(uploaded);
            //}

            // ── Аудио (музыка) — AudioBypass открывает audio.getUploadServer ──
            //if (tgMsg.Audio != null)
            //{
            //    var name     = $"{tgMsg.Audio.Performer} - {tgMsg.Audio.Title}".Trim(' ', '-') ?? tgMsg.Audio.FileName;
            //    var trueName = string.IsNullOrEmpty(name) ? tgMsg.Audio.FileName : name;
            //    var uploaded = await UploadAudioAsync(tgMsg.Audio.FileId, trueName, ct);
            //    if (uploaded != null) attachments.Add(uploaded);
            //}

            // ── Видео ─────────────────────────────────────────────────────────
            if (tgMsg.Video != null)
            {
                var uploaded = await UploadVideoAsync(
                    tgMsg.Video.FileId,
                    tgMsg.Video.FileName ?? "video.mp4", ct);
                if (uploaded != null) attachments.Add(uploaded);
            }

            // ── Видеокружок ───────────────────────────────────────────────────
            if (tgMsg.VideoNote != null)
            {
                var uploaded = await UploadVideoAsync(tgMsg.VideoNote.FileId, "video_note.mp4", ct);
                if (uploaded != null) attachments.Add(uploaded);
            }


            return attachments;
        }

        private async Task<VkNet.Model.Document?> UploadDocumentBytesAsync(
                long peerId, byte[] bytes, string fileName, CancellationToken ct)
        {
            try
            {
                // Получаем upload server через HTTP с групповым токеном
                var serverUrl = $"https://api.vk.com/method/docs.getMessagesUploadServer" +
                                $"?peer_id={peerId}&type=doc&access_token={_options.Vk.VkGroupToken}&v=5.199";
                var serverResp = await _http.GetAsync(serverUrl, ct);
                var serverJson = await serverResp.Content.ReadAsStringAsync(ct);

                using var serverDoc = System.Text.Json.JsonDocument.Parse(serverJson);
                var uploadUrl = serverDoc.RootElement
                    .GetProperty("response")
                    .GetProperty("upload_url")
                    .GetString()!;

                // Загружаем файл
                var ext = Path.GetExtension(fileName).ToLower();
                var mimeType = ext switch
                {
                    ".gif" => "image/gif",
                    ".webm" => "video/webm",
                    ".pdf" => "application/pdf",
                    ".mp3" => "audio/mpeg",
                    _ => "application/octet-stream"
                };

                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(bytes);
                content.Add(fileContent, "file", fileName);

                var uploadResp = await _http.PostAsync(uploadUrl, content, ct);
                var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);

                using var uploadDoc = System.Text.Json.JsonDocument.Parse(uploadJson);
                var file = uploadDoc.RootElement.GetProperty("file").GetString()!;

                // Сохраняем с групповым токеном
                using var saveContent = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "file",         file },
                        { "title",        fileName },
                        { "access_token", _options.Vk.VkGroupToken },
                        { "v",            "5.199" }
                    });

                var saveResp = await _http.PostAsync("https://api.vk.com/method/docs.save", saveContent, ct);
                var saveJson = await saveResp.Content.ReadAsStringAsync(ct);

                using var saveDoc = System.Text.Json.JsonDocument.Parse(saveJson);
                var docEl = saveDoc.RootElement
                    .GetProperty("response")
                    .GetProperty("doc");

                return new VkNet.Model.Document
                {
                    Id = docEl.GetProperty("id").GetInt64(),
                    OwnerId = docEl.GetProperty("owner_id").GetInt64(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки документа в VK (file={Name})", fileName);
                return null;
            }
        }

        private async Task<byte[]?> ConvertWebmToGifAsync(byte[] webmBytes, CancellationToken ct)
        {
            var tempWebm = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.webm");
            var tempGif = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.gif");
            try
            {
                await File.WriteAllBytesAsync(tempWebm, webmBytes, ct);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-i \"{tempWebm}\" -vf \"fps=15,scale=320:-1:flags=lanczos\" \"{tempGif}\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi)!;
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    var err = await process.StandardError.ReadToEndAsync(ct);
                    _logger.LogError("ffmpeg завершился с ошибкой: {Error}", err);
                    return null;
                }

                return await File.ReadAllBytesAsync(tempGif, ct);
            }
            finally
            {
                if (File.Exists(tempWebm)) File.Delete(tempWebm);
                if (File.Exists(tempGif)) File.Delete(tempGif);
            }
        }


        // ── Фото ──────────────────────────────────────────────────────────────
        private async Task<VkNet.Model.Photo?> UploadPhotoAsync(
            long peerId, string tgFileId, CancellationToken ct)
        {
            try
            {
                var (bytes, name) = await DownloadTgFileAsync(tgFileId, "photo.jpg", ct);

                // Получаем upload server напрямую через Kate Mobile токен
                var serverUrl = $"https://api.vk.com/method/photos.getMessagesUploadServer" +
                                $"?peer_id={peerId}&access_token={_options.Vk.KateMobileToken}&v=5.199";
                var serverResp = await _http.GetAsync(serverUrl, ct);
                var serverJson = await serverResp.Content.ReadAsStringAsync(ct);

                using var serverDoc = System.Text.Json.JsonDocument.Parse(serverJson);
                var uploadUrl = serverDoc.RootElement
                    .GetProperty("response")
                    .GetProperty("upload_url")
                    .GetString()!;

                // Retry до 3 раз при ошибке upload
                string uploadJson = "";
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    using var content = new MultipartFormDataContent();
                    var fileContent = new ByteArrayContent(bytes);
                    content.Add(fileContent, "photo", name);

                    var uploadResp = await _http.PostAsync(uploadUrl, content, ct);

                    if (uploadResp.IsSuccessStatusCode)
                    {
                        uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);
                        break;
                    }

                    _logger.LogDebug("Upload фото вернул {Status}, попытка {Attempt}/3",
                        (int)uploadResp.StatusCode, attempt);

                    if (attempt < 3)
                    {
                        await Task.Delay(1000 * attempt, ct);

                        // Получаем новый upload server
                        serverResp = await _http.GetAsync(serverUrl, ct);
                        serverJson = await serverResp.Content.ReadAsStringAsync(ct);
                        using var retryDoc = System.Text.Json.JsonDocument.Parse(serverJson);
                        uploadUrl = retryDoc.RootElement
                            .GetProperty("response")
                            .GetProperty("upload_url")
                            .GetString()!;
                    }
                    else
                    {
                        _logger.LogError("Не удалось загрузить фото после 3 попыток");
                        return null;
                    }
                }

                using var doc = System.Text.Json.JsonDocument.Parse(uploadJson);
                var root = doc.RootElement;
                var server = root.GetProperty("server").GetInt64();
                var photo = root.GetProperty("photo").GetString()!;
                var hash = root.GetProperty("hash").GetString()!;

                using var saveContent = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "server",       server.ToString() },
                        { "photo",        photo },
                        { "hash",         hash },
                        { "access_token", _options.Vk.KateMobileToken },
                        { "v",            "5.199" }
                    });

                var saveResp = await _http.PostAsync("https://api.vk.com/method/photos.saveMessagesPhoto", saveContent, ct);
                var saveJson = await saveResp.Content.ReadAsStringAsync(ct);

                using var saveDoc = System.Text.Json.JsonDocument.Parse(saveJson);
                var first = saveDoc.RootElement.GetProperty("response")[0];

                return new VkNet.Model.Photo
                {
                    Id = first.GetProperty("id").GetInt64(),
                    OwnerId = first.GetProperty("owner_id").GetInt64(),
                    AccessKey = first.TryGetProperty("access_key", out var ak) ? ak.GetString() : null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки фото в VK");
                return null;
            }
        }

        // ── Документ (файл / стикер / gif) ────────────────────────────────────
        private async Task<VkNet.Model.Document?> UploadDocumentAsync(
            long peerId, string tgFileId, string fileName, CancellationToken ct)
        {
            try
            {
                var (bytes, _) = await DownloadTgFileAsync(tgFileId, fileName, ct);
                var ext = Path.GetExtension(fileName);
                var safeName = $"document{ext}";

                // Получаем upload server через HTTP с групповым токеном
                var serverUrl = $"https://api.vk.com/method/docs.getMessagesUploadServer" +
                                $"?peer_id={peerId}&type=doc&access_token={_options.Vk.VkGroupToken}&v=5.199";
                var serverResp = await _http.GetAsync(serverUrl, ct);
                var serverJson = await serverResp.Content.ReadAsStringAsync(ct);

                using var serverDoc = System.Text.Json.JsonDocument.Parse(serverJson);
                var uploadUrl = serverDoc.RootElement
                    .GetProperty("response")
                    .GetProperty("upload_url")
                    .GetString()!;

                // Загружаем файл
                var mimeType = ext.ToLower() switch
                {
                    ".pdf" => "application/pdf",
                    ".webm" => "video/webm",
                    ".mp3" => "audio/mpeg",
                    ".jpg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    _ => "application/octet-stream"
                };

                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                content.Add(fileContent, "file", safeName);

                var uploadResp = await _http.PostAsync(uploadUrl, content, ct);
                var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);

                using var uploadDoc = System.Text.Json.JsonDocument.Parse(uploadJson);
                var file = uploadDoc.RootElement.GetProperty("file").GetString()!;

                // Сохраняем документ с групповым токеном
                using var saveContent = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "file",         file },
                        { "title",        safeName },
                        { "access_token", _options.Vk.VkGroupToken },
                        { "v",            "5.199" }
                    });

                var saveResp = await _http.PostAsync("https://api.vk.com/method/docs.save", saveContent, ct);
                var saveJson = await saveResp.Content.ReadAsStringAsync(ct);

                _logger.LogDebug("docs.save response: {Json}", saveJson);

                using var saveDoc = System.Text.Json.JsonDocument.Parse(saveJson);
                var docEl = saveDoc.RootElement
                    .GetProperty("response")
                    .GetProperty("doc");

                var savedDoc = new VkNet.Model.Document
                {
                    Id = docEl.GetProperty("id").GetInt64(),
                    OwnerId = docEl.GetProperty("owner_id").GetInt64(),
                };

                _logger.LogInformation("Документ загружен: id={Id}, ownerId={OwnerId}", savedDoc.Id, savedDoc.OwnerId);
                return savedDoc;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки документа в VK (file={Name})", fileName);
                return null;
            }
        }

        // ── Голосовое сообщение ───────────────────────────────────────────────
        private async Task<VkNet.Model.AudioMessage?> UploadVoiceAsync(
            long peerId, string tgFileId, CancellationToken ct)
        {
            try
            {
                var (bytes, _) = await DownloadTgFileAsync(tgFileId, "voice.ogg", ct);

                var uploadServer = await _vkApi.Docs.GetMessagesUploadServerAsync(peerId, VkNet.Enums.StringEnums.DocMessageType.AudioMessage, ct);
                using var content = new MultipartFormDataContent();
                content.Add(new ByteArrayContent(bytes), "file", "voice.ogg");

                var uploadResp = await _http.PostAsync(uploadServer.UploadUrl, content, ct);
                var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);

                var saved = _vkApi.Docs.Save(uploadJson, "voice.ogg");
                return saved.FirstOrDefault()?.Instance as VkNet.Model.AudioMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки голосового сообщения в VK");
                return null;
            }
        }

        // ── Аудио (музыка) через AudioBypass ─────────────────────────────────
        // AudioBypass разблокирует audio.getUploadServer и audio.save,
        // недоступные для обычного community-токена.
        private async Task<VkNet.Model.Audio?> UploadAudioAsync(
            string tgFileId, string trackName, CancellationToken ct)
        {
            try
            {
                var (bytes, resolvedName) = await DownloadTgFileAsync(tgFileId, $"{trackName}.mp3", ct);


                var name = !string.IsNullOrWhiteSpace(trackName) ? $"{trackName}.mp3" : resolvedName;

                // Получаем upload server напрямую через Kate Mobile токен
                var serverUrl = $"https://api.vk.com/method/audio.getUploadServer" +
                                 $"?access_token={_options.Vk.KateMobileToken}&v=5.199";
                var serverResp = await _http.GetAsync(serverUrl, ct);
                var serverJson = await serverResp.Content.ReadAsStringAsync(ct);

                using var serverDoc = System.Text.Json.JsonDocument.Parse(serverJson);
                var uploadUrl = serverDoc.RootElement
                    .GetProperty("response")
                    .GetProperty("upload_url")
                    .GetString()!;

                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                content.Add(fileContent, "file", name);

                var uploadResp = await _http.PostAsync(uploadUrl, content, ct);
                var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);

                // Сохраняем через HTTP напрямую
                var parts = trackName.Split(" - ", 2, StringSplitOptions.TrimEntries);
                var artist = parts.Length == 2 ? parts[0] : string.Empty;
                var title = parts.Length == 2 ? parts[1] : trackName;

                using var uploadDoc = System.Text.Json.JsonDocument.Parse(uploadJson);
                var server = uploadDoc.RootElement.GetProperty("server").GetString()!;
                var audio = uploadDoc.RootElement.GetProperty("audio").GetString()!;
                var hash = uploadDoc.RootElement.GetProperty("hash").GetString()!;

                using var saveContent = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "server",       server },
                        { "audio",        audio  },
                        { "hash",         hash   },
                        { "artist",       artist },
                        { "title",        title  },
                        { "access_token", _options.Vk.KateMobileToken },
                        { "v",            "5.199" }
                    });

                var saveResp = await _http.PostAsync("https://api.vk.com/method/audio.save", saveContent, ct);
                var saveJson = await saveResp.Content.ReadAsStringAsync(ct);

                _logger.LogDebug("audio.save response: {Json}", saveJson);

                using var saveDoc = System.Text.Json.JsonDocument.Parse(saveJson);
                var audioEl = saveDoc.RootElement.GetProperty("response");

                return new VkNet.Model.Audio
                {
                    Id = audioEl.GetProperty("id").GetInt64(),
                    OwnerId = audioEl.GetProperty("owner_id").GetInt64(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки аудио в VK (track={Track})", trackName);
                return null;
            }
        }

        // ── Видео ─────────────────────────────────────────────────────────────
        private async Task<VkNet.Model.Video?> UploadVideoAsync(
    string tgFileId, string fileName, CancellationToken ct)
        {
            try
            {
                var (bytes, resolvedName) = await DownloadTgFileAsync(tgFileId, fileName, ct);
                var name = !string.IsNullOrWhiteSpace(fileName) ? fileName : resolvedName;
                var nameWithoutExt = Path.GetFileNameWithoutExtension(name);

                // Шаг 1: резервируем слот через Kate token
                var saveServerUrl = "https://api.vk.com/method/video.save"
                                  + $"?name={Uri.EscapeDataString(nameWithoutExt)}"
                                  + "&is_private=0"
                                  + "&wallpost=0"
                                  + $"&access_token={_options.Vk.KateMobileToken}"
                                  + "&v=5.199";

                var saveServerResp = await _http.GetAsync(saveServerUrl, ct);
                var saveServerJson = await saveServerResp.Content.ReadAsStringAsync(ct);

                _logger.LogDebug("video.save response: {Json}", saveServerJson);

                using var saveServerDoc = System.Text.Json.JsonDocument.Parse(saveServerJson);

                if (saveServerDoc.RootElement.TryGetProperty("error", out var saveErr))
                {
                    _logger.LogError("VK ошибка при video.save: {Err}", saveErr.ToString());
                    return null;
                }

                var response = saveServerDoc.RootElement.GetProperty("response");
                var uploadUrl = response.GetProperty("upload_url").GetString()!;
                var ownerId = response.GetProperty("owner_id").GetInt64();
                var videoId = response.GetProperty("video_id").GetInt64();

                // Шаг 2: заливаем файл
                using var content = new MultipartFormDataContent();
                content.Add(new ByteArrayContent(bytes), "video_file", name);
                var uploadResp = await _http.PostAsync(uploadUrl, content, ct);
                var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);

                await Task.Delay(2000, ct);

                // Шаг 3: читаем сохранённый объект через Kate token
                var getUrl = "https://api.vk.com/method/video.get"
                           + $"?videos={ownerId}_{videoId}"
                           + $"&access_token={_options.Vk.KateMobileToken}"
                           + "&v=5.199";

                var getResp = await _http.GetAsync(getUrl, ct);
                var getJson = await getResp.Content.ReadAsStringAsync(ct);

                _logger.LogDebug("video.get response: {Json}", getJson);

                using var getDoc = System.Text.Json.JsonDocument.Parse(getJson);

                if (getDoc.RootElement.TryGetProperty("error", out var getErr))
                {
                    _logger.LogError("VK ошибка при video.get: {Err}", getErr.ToString());
                    return null;
                }

                var items = getDoc.RootElement.GetProperty("response").GetProperty("items");
                if (items.GetArrayLength() == 0) return null;

                var item = items[0];
                return new VkNet.Model.Video
                {
                    Id = item.GetProperty("id").GetInt64(),
                    OwnerId = item.GetProperty("owner_id").GetInt64(),
                    Title = item.TryGetProperty("title", out var t) ? t.GetString() : null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки видео в VK (file={Name})", fileName);
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Вспомогательные методы
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Скачивает файл из Telegram по fileId через Bot API.</summary>
        private async Task<(byte[] Bytes, string FileName)> DownloadTgFileAsync(
            string fileId, string defaultName, CancellationToken ct)
        {
            if (_botClient is null)
                throw new InvalidOperationException("BotClient не инициализирован — вызовите SetBotClient() до начала работы.");

            var file  = await _botClient.GetFile(fileId, ct);
            var url   = $"https://api.telegram.org/file/bot{_options.TelegramBot.TgToken}/{file.FilePath}";
            var bytes = await _http.GetByteArrayAsync(url, ct);
            var name  = Path.GetFileName(file.FilePath!) is { Length: > 0 } n ? n : defaultName;
            return (bytes, name);
        }

        private async Task<string?> ResolveVideoUrlAsync(VkNet.Model.Video video, CancellationToken ct)
        {
            if (video.OwnerId == null || video.Id == null) return null;

            var videoId = $"{video.OwnerId}_{video.Id}";
            if (!string.IsNullOrEmpty(video.AccessKey))
                videoId += $"_{video.AccessKey}";

            var url = "https://api.vk.com/method/video.get"
                    + $"?videos={videoId}"
                    + $"&access_token={_options.Vk.KateMobileToken}"
                    + "&v=5.199";

            var json = await _http.GetStringAsync(url, ct);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("response", out var response)) return null;
            if (!response.TryGetProperty("items", out var items)) return null;
            if (items.GetArrayLength() == 0) return null;

            var item = items[0];

            // Собираем Video вручную — только то, что нужно для GetBestVideoUrl
            var files = new VkNet.Model.VideoFiles();
            if (item.TryGetProperty("files", out var f))
            {
                if (f.TryGetProperty("mp4_1080", out var v)) files.Mp4_1080 = new Uri(v.GetString()!);
                if (f.TryGetProperty("mp4_720", out v)) files.Mp4_720 = new Uri(v.GetString()!);
                if (f.TryGetProperty("mp4_480", out v)) files.Mp4_480 = new Uri(v.GetString()!);
                if (f.TryGetProperty("mp4_360", out v)) files.Mp4_360 = new Uri(v.GetString()!);
                if (f.TryGetProperty("mp4_240", out v)) files.Mp4_240 = new Uri(v.GetString()!);
                if (f.TryGetProperty("external", out v)) files.External = new Uri(v.GetString()!);
            }

            var fetched = new VkNet.Model.Video { Files = files };
            return fetched != null ? GetBestVideoUrl(fetched) : null;
        }

        private static string? GetBestStickerUrl(VkNet.Model.Sticker sticker)
        {
            var images = sticker.Images ?? sticker.ImagesWithBackground;
            if (images is null || !images.Any()) return null;

            return images
                .OrderByDescending(img => img.Width)
                .FirstOrDefault()
                ?.Url?.ToString();
        }

        private static string? GetBestVideoUrl(VkNet.Model.Video video)
        {
            if (video.Files is null) return null;
            return video?.Files?.Mp4_1080?.ToString()
                 ?? video?.Files?.Mp4_720?.ToString()
                 ?? video?.Files?.Mp4_480?.ToString()
                 ?? video?.Files?.Mp4_360?.ToString()
                 ?? video?.Files?.Mp4_240?.ToString()
                 ?? video?.Files?.External?.ToString();
        }

        private static Uri? GetBestPhotoUrl(VkNet.Model.Photo photo)
        {
            if (photo.Sizes is null || photo.Sizes.Count == 0) return null;
            foreach (var t in new[] { "w", "z", "y", "x", "r", "q", "p", "o", "m", "s" })
            {
                var size = photo.Sizes.FirstOrDefault(s => s.Type.ToString() == t);
                if (size?.Url != null) return size.Url;
            }
            return photo.Sizes.Last().Url;
        }

        private async Task<InputFile> DownloadToStreamAsync(string url, string fileName, CancellationToken ct)
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            return InputFile.FromStream(new MemoryStream(bytes), fileName);
        }

        private static void TrySetCaption(InputMedia media, string? caption, ref bool used)
        {
            if (used || string.IsNullOrEmpty(caption)) return;
            media.Caption = caption;
            used = true;
        }
    }
}
