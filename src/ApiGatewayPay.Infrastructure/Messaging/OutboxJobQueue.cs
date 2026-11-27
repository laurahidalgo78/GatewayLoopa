using System.Text.Json;
using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Infrastructure.Persistence;
using ApiGatewayPay.Infrastructure.Platform;

namespace ApiGatewayPay.Infrastructure.Messaging;

/// <summary>
/// Cola durable sobre la propia base de datos (outbox transaccional). Para escalar, un relay puede
/// publicar estas filas en RabbitMQ/Azure Service Bus/SQS sin cambiar el núcleo.
/// </summary>
public sealed class OutboxJobQueue(AppDbContext db, TimeProvider time) : IJobQueue
{
    public void Enqueue<TPayload>(string jobType, TPayload payload, TimeSpan? delay = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        db.Jobs.Add(new JobEntity
        {
            Id = Guid.CreateVersion7(),
            Type = jobType,
            Payload = JsonSerializer.Serialize(payload),
            Status = JobStatus.Pending,
            AvailableAt = now + (delay ?? TimeSpan.Zero),
            CorrelationId = CorrelationContext.Id ?? CorrelationContext.NewId(),
            CreatedAt = now,
            UpdatedAt = now,
        });
    }
}
