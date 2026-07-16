using System.Text.Json;

namespace C3OSS.Codexcw;

/// <summary>Turns one JSONL line into zero or more decoded events.</summary>
internal interface IEventDecoder
{
    IReadOnlyList<Event> Decode(string line, string runId, string threadId, DateTimeOffset now);
}

internal sealed class CodexDecoder : IEventDecoder
{
    public IReadOnlyList<Event> Decode(string line, string runId, string threadId, DateTimeOffset now) =>
        [DecodeEvent(line, runId, threadId, now)];

    internal static Event DecodeEvent(string line, string runId, string threadId, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.GetString("type");
        if (type.Length == 0)
        {
            throw new FormatException("missing event type");
        }

        var @event = new Event
        {
            Kind = EventTypes.KindOf(type),
            Type = type,
            RunId = runId,
            ThreadId = threadId,
            ReceivedAt = now,
            Raw = line,
        };

        return type switch
        {
            EventTypes.ThreadStarted => @event with
            {
                ThreadId = root.GetString("thread_id"),
                ThreadStarted = new ThreadStartedPayload(root.GetString("thread_id")),
            },
            EventTypes.TurnCompleted => @event with
            {
                TurnCompleted = new TurnCompletedPayload(ParseUsage(root.GetObject("usage"))),
            },
            EventTypes.TurnFailed => @event with
            {
                TurnFailed = new TurnFailedPayload(ParseErrorPayload(root.GetElement("error")), new Usage()),
            },
            EventTypes.ItemStarted => @event with
            {
                ItemStarted = new ItemPayload(DecodeItem(root.GetElement("item"))),
            },
            EventTypes.ItemCompleted => @event with
            {
                ItemCompleted = new ItemPayload(DecodeItem(root.GetElement("item"))),
            },
            EventTypes.Error => @event with
            {
                Error = ParseTopLevelError(root),
            },
            _ => @event,
        };
    }

    internal static Item DecodeItem(JsonElement? item)
    {
        if (item is not { } wire)
        {
            throw new FormatException("missing item payload");
        }
        if (wire.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("item payload is not an object");
        }
        var type = wire.GetString("type");
        int? exitCode = wire.GetElement("exit_code") is { ValueKind: JsonValueKind.Number } code
            ? code.GetInt32()
            : null;
        return new Item
        {
            Id = wire.GetString("id"),
            Kind = ItemTypes.KindOf(type),
            Type = type,
            Status = wire.GetString("status"),
            Raw = wire.GetRawText(),
            Text = wire.GetString("text"),
            Message = wire.GetString("message"),
            Command = wire.GetString("command"),
            AggregatedOutput = wire.GetString("aggregated_output"),
            ExitCode = exitCode,
            Changes = ParseChanges(wire.GetElement("changes")),
        };
    }

    private static List<FileChange> ParseChanges(JsonElement? changes)
    {
        if (changes is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }
        var parsed = new List<FileChange>(array.GetArrayLength());
        foreach (var change in array.EnumerateArray())
        {
            parsed.Add(new FileChange(change.GetString("path"), change.GetString("kind")));
        }
        return parsed;
    }

    internal static ErrorPayload ParseErrorPayload(JsonElement? error)
    {
        if (error is not { } wire)
        {
            return new ErrorPayload();
        }
        var raw = wire.GetRawText();
        var message = wire.GetString("message");
        if (message.Length == 0)
        {
            message = raw;
        }
        return new ErrorPayload { Message = message, Raw = raw };
    }

    private static ErrorPayload ParseTopLevelError(JsonElement root)
    {
        var raw = root.GetElement("error")?.GetRawText() ?? "";
        var message = root.GetString("message");
        if (message.Length == 0 && raw.Length > 0)
        {
            message = raw;
        }
        return new ErrorPayload { Message = message, Raw = raw };
    }

    internal static Usage ParseUsage(JsonElement? usage)
    {
        if (usage is not { } wire)
        {
            return new Usage();
        }
        return new Usage
        {
            InputTokens = wire.GetLong("input_tokens"),
            CachedInputTokens = wire.GetLong("cached_input_tokens"),
            CacheCreationInputTokens = wire.GetLong("cache_creation_input_tokens"),
            OutputTokens = wire.GetLong("output_tokens"),
            ReasoningOutputTokens = wire.GetLong("reasoning_output_tokens"),
            TotalTokens = wire.GetLong("total_tokens"),
            TotalCostUsd = wire.GetDouble("total_cost_usd"),
            ModelUsage = ParseModelUsage(wire.GetObject("model_usage")),
        };
    }

    private static Dictionary<string, ModelUsage> ParseModelUsage(JsonElement? modelUsage)
    {
        var parsed = new Dictionary<string, ModelUsage>();
        if (modelUsage is not { } wire)
        {
            return parsed;
        }
        foreach (var entry in wire.EnumerateObject())
        {
            var model = entry.Value;
            parsed[entry.Name] = new ModelUsage
            {
                InputTokens = model.GetLong("input_tokens"),
                OutputTokens = model.GetLong("output_tokens"),
                CacheReadInputTokens = model.GetLong("cache_read_input_tokens"),
                CacheCreationInputTokens = model.GetLong("cache_creation_input_tokens"),
                WebSearchRequests = model.GetLong("web_search_requests"),
                CostUsd = model.GetDouble("cost_usd"),
                ContextWindow = model.GetLong("context_window"),
                MaxOutputTokens = model.GetLong("max_output_tokens"),
            };
        }
        return parsed;
    }
}
