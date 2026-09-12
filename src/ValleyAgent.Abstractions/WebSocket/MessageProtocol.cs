using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValleyAgent.WebSocket
{
    /// <summary>
    /// Thread-safe JSON serialization helpers for the WebSocket protocol.
    /// All messages use camelCase property naming and string-based enum conversion.
    /// </summary>
    public static class MessageProtocol
    {
        // 枚举字符串统一以 camelCase 序列化（如 "brewer", "fishingStreak"），
        // 与 TS 端 narrative-types.ts 的 lowercase / camelCase 字符串联合类型对齐。
        // 现有 WebSocket 消息类型 (DecisionResponse/DialogueResponse 等) 不含枚举字段，
        // 因此切换为 camelCase 不会破坏现有协议。
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Serialize a message object to a compact JSON string.
        /// </summary>
        /// <typeparam name="T">The message type.</typeparam>
        /// <param name="message">The message instance to serialize.</param>
        /// <returns>A JSON string representation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if serialization fails.</exception>
        public static string Serialize<T>(T message)
        {
            try
            {
                return JsonSerializer.Serialize(message, Options);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to serialize message of type {typeof(T).Name}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Deserialize a JSON string to the specified message type.
        /// </summary>
        /// <typeparam name="T">The target message type.</typeparam>
        /// <param name="json">The JSON string to deserialize.</param>
        /// <returns>The deserialized message instance.</returns>
        /// <exception cref="InvalidOperationException">Thrown if deserialization fails or the JSON is null/empty.</exception>
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentNullException(nameof(json), "JSON string cannot be null or empty.");
            }

            try
            {
                var result = JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException(
                        $"Deserialization of type {typeof(T).Name} returned null.");
                return result;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to deserialize JSON to type {typeof(T).Name}: {ex.Message}", ex);
            }
        }
    }
}
