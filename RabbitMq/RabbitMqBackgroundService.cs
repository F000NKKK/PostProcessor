using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using NLog;

namespace PostProcessor.RabbitMq
{
    public class PostProcessorBackgroundService : BackgroundService, IRabbitMqService, IRabbitMqBackgroundService
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
        private readonly IConnection _connection;
        private readonly IModel _channel;
        private readonly IServiceProvider _serviceProvider;

        private const string WebCompBotQueueName = "WebCompBotQueue";
        private const string FromPreProcessor = "PostProcessorQueue";

        public PostProcessorBackgroundService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;

            var factory = new ConnectionFactory() { HostName = "localhost" };
            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            _channel.QueueDeclare(queue: WebCompBotQueueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
            _channel.QueueDeclare(queue: FromPreProcessor, durable: true, exclusive: false, autoDelete: false, arguments: null);

            _channel.BasicQos(0, 1, false);
        }

        public void SendMessageToQueue(string queueName, string message)
        {
            var body = Encoding.UTF8.GetBytes(message);
            _channel.BasicPublish(exchange: "", routingKey: queueName, basicProperties: null, body: body);
            Logger.Info($"Сообщение отправлено в очередь '{queueName}'");
        }

        public async Task AcknowledgeMessage(ulong deliveryTag)
        {
            await Task.Run(() => _channel.BasicAck(deliveryTag, false));
        }

        public async Task RejectMessage(ulong deliveryTag, bool requeue)
        {
            await Task.Run(() => _channel.BasicReject(deliveryTag, requeue));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.Info($"Запуск обработки сообщений из очереди '{FromPreProcessor}'");
            await ProcessMessagesAsync(stoppingToken);
        }

        public async Task ProcessMessagesAsync(CancellationToken cancellationToken)
        {
            var consumer = new EventingBasicConsumer(_channel);

            consumer.Received += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                var deserializedMessage = JsonSerializer.Deserialize<Message>(message);

                var flag = (deserializedMessage.Content != null) ? "NotNull" : "Null";
                Logger.Info($"\nПолучено сообщение:\nId: {deserializedMessage.Id}\nContent: {flag}\nMessageCurrentTime: {deserializedMessage.MessageCurrentTime}");

                try
                {
                    Logger.Info($"Начало обработки сообщения с ID: {deserializedMessage.Id}");

                    // Разбиение сообщения на части
                    var messageParts = SplitMessage(deserializedMessage.Content, 200);

                    string id = deserializedMessage.Id;

                    int k = 0;
                    foreach (var part in messageParts)
                    {
                        deserializedMessage.Content = part;
                        deserializedMessage.MessageCurrentTime = DateTime.Now.ToString("dd:MM:yyyy HH:mm:ss");
                        deserializedMessage.Id = id + "~" + k;
                        k++;

                        SendMessageToQueue(WebCompBotQueueName, JsonSerializer.Serialize(deserializedMessage));
                        Logger.Info($"Отправлена {k} часть сообщения в очередь '{WebCompBotQueueName}'");
                    }

                    // Подтверждение сообщения
                    await AcknowledgeMessage(ea.DeliveryTag);
                    Logger.Info($"Сообщение подтверждено с deliveryTag: {ea.DeliveryTag}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Произошла ошибка при обработке сообщения с ID {deserializedMessage.Id}: {ex.Message}");
                    // Отклонение сообщения в случае ошибки
                    await RejectMessage(ea.DeliveryTag, true);
                }
            };

            _channel.BasicConsume(queue: FromPreProcessor, autoAck: false, consumer: consumer);

            // Ожидание отмены задачи
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public override void Dispose()
        {
            try
            {
                _channel?.Close();
                _connection?.Close();
                Logger.Info("Соединение с RabbitMQ закрыто");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка при закрытии соединения: {ex.Message}");
            }
            finally
            {
                base.Dispose();
            }
        }


        private List<string> SplitMessage(string message, int maxLength)
        {
            var messageParts = new List<string>();

            // Разделители для разбиения сообщения
            var delimiters = new[] { '.', '!', '?' };

            while (message.Length > maxLength)
            {
                // Найти подходящий разделитель в пределах максимальной длины
                var splitIndex = message.LastIndexOfAny(delimiters, maxLength);

                // Если не удалось найти разделитель, разбиваем на фиксированную длину
                if (splitIndex == -1)
                {
                    splitIndex = maxLength;
                }

                // Если разделитель найден, включаем его в сообщение
                if (delimiters.Contains(message[splitIndex]))
                {
                    splitIndex++;
                }

                // Убираем пробелы перед и после сообщения
                var part = message.Substring(0, splitIndex).Trim();
                messageParts.Add(part);

                // Оставшаяся часть сообщения
                message = message.Substring(splitIndex).Trim();
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                messageParts.Add(message);
            }

            return messageParts;
        }

        // Класс для представления сообщения.
        public class Message
        {
            public string Id { get; set; } = string.Empty; // Идентификатор сообщения
            public string Content { get; set; } = string.Empty; // Содержимое сообщения
            public string MessageCurrentTime { get; set; } = string.Empty; // Время отправки сообщения
            public Boolean IsUserMessage { get; set; } = true; // Флаг User/Bot, True/False соответственно
        }
    }
}
