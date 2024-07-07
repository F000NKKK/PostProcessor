using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PostProcessor.RabbitMq;
using Microsoft.AspNetCore.Builder;

namespace PostProcessor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddScoped<IRabbitMqService, PostProcessorBackgroundService>();
            builder.Services.AddScoped<IRabbitMqBackgroundService, PostProcessorBackgroundService>();
            builder.Services.AddHostedService<PostProcessorBackgroundService>();

            builder.Services.AddSignalR();

            var app = builder.Build();

            app.Run();

        }
    }
}