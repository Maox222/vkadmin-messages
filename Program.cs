using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Telegram.Bot;
using vkadmin_msg.Models;
using vkadmin_msg.Services;
using VkNet;
using VkNet.AudioBypassService.Extensions;

namespace vkadmin_msg
{
    class Program
    {
        static async Task Main(string[] args)
        {

            try
            {
                Log.Information("Запуск хоста приложения...");

                var builder = Host.CreateDefaultBuilder(args);

                // Отключаем reloadOnChange для файлов конфигурации
                builder.ConfigureAppConfiguration((hostingContext, config) =>
                {
                    // Очищаем стандартные провайдеры, чтобы они не дублировались
                    config.Sources.Clear();

                    var env = hostingContext.HostingEnvironment;

                    // Добавляем файлы заново с флагом reloadOnChange: false
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                          .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: false);

                    // Возвращаем переменные окружения и аргументы командной строки (если они нужны)
                    config.AddEnvironmentVariables();
                    if (args != null)
                    {
                        config.AddCommandLine(args);
                    }
                });


                builder.UseSerilog((context, services, configuration) => configuration
                    .MinimumLevel.Warning()
                    .MinimumLevel.Override("vkadmin_msg", Serilog.Events.LogEventLevel.Information)
                    .WriteTo.Console()
                    .WriteTo.File(
                        path: "log.txt",
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    ));

                builder.ConfigureServices((hostContext, services) =>
                {
                    services.Configure<BotOptions>(hostContext.Configuration.GetSection("BotConfig"));

                    services.AddSingleton<VkApi>(sp =>
                    {
                        // Получаем IServiceCollection через фабрику:
                        // AudioBypass требует именно ServiceCollection, а не Provider.
                        // Создаём отдельную коллекцию только для VkApi, копируем туда
                        // нужные дескрипторы из основного контейнера.
                        var vkServices = new ServiceCollection();
                        vkServices.AddAudioBypass();
                        // Пробрасываем логгер из основного DI-контейнера
                        vkServices.AddLogging(b => b.AddSerilog(Log.Logger));


                        return new VkApi(vkServices);
                    });

                    // ── НАСТРОЙКА БОТА 1 ──
                    services.AddHttpClient("Bot1Client"); 
                    services.AddSingleton<ITelegramBotClient>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfiguration>();
                        var factory = sp.GetRequiredService<IHttpClientFactory>();

                        var token = config["BotConfig:TelegramBot:TgToken"];
                        if (string.IsNullOrEmpty(token)) throw new Exception("Токен \"TgToken\" не найден!");

                        return new TelegramBotClient(token, factory.CreateClient("Bot1Client"));
                    });
                    // ── НАСТРОЙКА БОТА 2 ──
                    services.AddHttpClient("Bot2Client");
                    services.AddKeyedSingleton<ITelegramBotClient>("replyBot", (sp, _) =>
                    {
                        var config = sp.GetRequiredService<IConfiguration>();
                        var factory = sp.GetRequiredService<IHttpClientFactory>();

                        var enabled = config.GetValue<bool>("BotConfig:TelegramBot:AllowReply");
                        if (!enabled) return null!;

                        var token = config["BotConfig:TelegramBot:SecondTgToken"];
                        if (string.IsNullOrEmpty(token)) throw new Exception("Токен \"SecondTgToken\" не найден!");

                        return new TelegramBotClient(token, factory.CreateClient("Bot2Client"));
                    });

                    // ── HttpClient для скачивания медиафайлов ────────────────────
                    services.AddHttpClient<AttachmentConverter>(client =>
                    {
                        client.Timeout = TimeSpan.FromSeconds(60);
                        // VK иногда возвращает 403 без User-Agent
                        client.DefaultRequestHeaders.UserAgent.ParseAdd(
                            "KateMobileAndroid/130.2-v942454 (Android 13; SDK 33; arm64-v8a; Xiaomi; ru)");
                    });

                    services.AddSingleton<MultiDataMap>();
                    services.AddSingleton<MessageBridge>();
                    services.AddSingleton<VkBot>(sp =>
                    {
                        var options = sp.GetRequiredService<IOptions<BotOptions>>().Value;
                        if (!options.Vk.AllowVkBot) return null!;

                        return ActivatorUtilities.CreateInstance<VkBot>(sp);
                    });

                    services.AddHostedService<TelegramService>();
                    services.AddHostedService<VkService>();
                });

                var host = builder.Build();
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Приложение аварийно завершило работу");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }
}
