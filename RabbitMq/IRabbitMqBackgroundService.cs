using System.Threading;
using System.Threading.Tasks;

namespace PostProcessor.RabbitMq
{
    public interface IRabbitMqBackgroundService
    {
        Task ProcessMessagesAsync(CancellationToken cancellationToken);
    }
}
