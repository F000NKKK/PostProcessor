using Microsoft.Extensions.Hosting;

namespace PostProcessor.RabbitMq
{
    public interface IRabbitMqService : IHostedService, IDisposable
    {
        void SendMessageToQueue(string queueName, string message);
        void AcknowledgeMessage(ulong deliveryTag);
        void RejectMessage(ulong deliveryTag, bool requeue);
    }

}
