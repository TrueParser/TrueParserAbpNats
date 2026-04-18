using System;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Json;

namespace TrueParser.Abp.EventBus.Nats;

public class DefaultNatsEventSerializer : INatsEventSerializer, ISingletonDependency
{
    protected IJsonSerializer JsonSerializer { get; }

    public DefaultNatsEventSerializer(IJsonSerializer jsonSerializer)
    {
        JsonSerializer = jsonSerializer;
    }

    public byte[] Serialize(object eventData)
    {
        return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(eventData));
    }

    public object Deserialize(byte[] value, Type type)
    {
        return JsonSerializer.Deserialize(type, System.Text.Encoding.UTF8.GetString(value));
    }

    public T Deserialize<T>(byte[] value)
    {
        return JsonSerializer.Deserialize<T>(System.Text.Encoding.UTF8.GetString(value));
    }
}
