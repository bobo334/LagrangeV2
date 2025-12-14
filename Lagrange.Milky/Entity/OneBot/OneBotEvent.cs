using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lagrange.Milky.Entity.OneBot;

public record OneBotPostEvent(
    [property: JsonPropertyName("post_type")] string PostType,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("self_id")] long SelfId,
    [property: JsonPropertyName("message_type")] string? MessageType = null,
    [property: JsonPropertyName("sub_type")] string? SubType = null,
    [property: JsonPropertyName("message_id")] long? MessageId = null,
    [property: JsonPropertyName("user_id")] long? UserId = null,
    [property: JsonPropertyName("group_id")] long? GroupId = null,
    [property: JsonPropertyName("message")] JsonElement? Message = null,
    [property: JsonPropertyName("raw_event")] JsonElement? RawEvent = null
);

public record OneBotApiResponse<T>(int retcode, string status, T? data);
