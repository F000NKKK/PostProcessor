using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using PostProcessor.RabbitMq;

namespace PostProcessor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Настройка NLog
            builder.Logging.ClearProviders();
            builder.Logging.AddNLog("nlog.config");

            // Регистрация RabbitMqService и фонового сервиса
            builder.Services.AddScoped<IRabbitMqService, PostProcessorBackgroundService>();
            builder.Services.AddScoped<IRabbitMqBackgroundService, PostProcessorBackgroundService>();
            builder.Services.AddHostedService<PostProcessorBackgroundService>();

            builder.Services.AddSignalR();

            var app = builder.Build();

            app.Run();

            // Завершение записи логов перед закрытием приложения
            LogManager.Shutdown();
        }
    }
}
