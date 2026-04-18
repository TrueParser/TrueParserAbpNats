using System;

namespace TrueParser.Abp.EventBus.Nats;

public interface INatsEventSerializer
{
    byte[] Serialize(object eventData);

    object Deserialize(byte[] value, Type type);

    T Deserialize<T>(byte[] value);
}
