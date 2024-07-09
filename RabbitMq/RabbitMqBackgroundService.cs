using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using NLog;
using System.Text.RegularExpressions;

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

            // Регулярное выражение для разбиения сообщения на части
            var pattern = $@"(.{{1,{maxLength}}})(?=\s|$)";
            var regex = new Regex(pattern, RegexOptions.Singleline);

            // Разбиваем сообщение на части
            foreach (Match match in regex.Matches(message))
            {
                messageParts.Add(match.Value); // Добавляем каждую часть
            }

            // Реконструируем сообщение из частей
            var reconstructedMessage = string.Join(" ", messageParts); // Добавляем пробел между частями

            // Убираем пробелы для проверки длины сообщений
            var originalMessageNoSpaces = message.Replace(" ", string.Empty);
            var reconstructedMessageNoSpaces = reconstructedMessage.Replace(" ", string.Empty);

            // Логирование длины для проверки
            Logger.Info($"Исходная длина сообщения: {message.Length}, Суммарная длина разделенных частей: {messageParts.Sum(part => part.Length)}");
            Logger.Info($"Длина оригинального сообщения без пробелов: {originalMessageNoSpaces.Length}");
            Logger.Info($"Длина реконструированного сообщения без пробелов: {reconstructedMessageNoSpaces.Length}");

            // Проверка соответствия длины оригинального и реконструированного сообщений
            if (reconstructedMessageNoSpaces.Length != originalMessageNoSpaces.Length)
            {
                Logger.Error($"Ошибка: суммарная длина частей ({messageParts.Sum(part => part.Length)}) не соответствует исходной длине сообщения ({message.Length}).");
                Logger.Error($"Длина оригинального сообщения: {message.Length}");
                Logger.Error($"Длина реконструированного сообщения: {reconstructedMessage.Length}");
                Logger.Error($"Длина оригинального сообщения без пробелов: {originalMessageNoSpaces.Length}");
                Logger.Error($"Длина реконструированного сообщения без пробелов: {reconstructedMessageNoSpaces.Length}");
                throw new InvalidOperationException("Суммарная длина частей не соответствует исходной длине сообщения.");
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
