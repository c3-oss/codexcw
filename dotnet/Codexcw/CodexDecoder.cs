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
        var type = root.GetStrictString("type");
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
                ThreadId = root.GetStrictString("thread_id"),
                ThreadStarted = new ThreadStartedPayload(root.GetStrictString("thread_id")),
            },
            EventTypes.TurnStarted => @event with
            {
                TurnStarted = new TurnStartedPayload(),
            },
            EventTypes.TurnCompleted => @event with
            {
                TurnCompleted = new TurnCompletedPayload(ParseUsage(UsageObject(root))),
            },
            EventTypes.TurnFailed => @event with
            {
                TurnFailed = new TurnFailedPayload(
                    ParseErrorPayload(root.GetElement("error")),
                    ParseUsage(UsageObject(root))),
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

    private static JsonElement? UsageObject(JsonElement root)
    {
        if (root.GetElement("usage") is not { } usage)
        {
            return null;
        }
        if (usage.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("field usage: expected object");
        }
        return usage;
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
        var type = wire.GetStrictString("type");
        return new Item
        {
            Id = wire.GetStrictString("id"),
            Kind = ItemTypes.KindOf(type),
            Type = type,
            Status = wire.GetStrictString("status"),
            Raw = wire.GetRawText(),
            Text = wire.GetStrictString("text"),
            Message = wire.GetStrictString("message"),
            Command = wire.GetStrictString("command"),
            AggregatedOutput = wire.GetStrictString("aggregated_output"),
            ExitCode = wire.GetStrictNullableInt("exit_code"),
            Changes = ParseChanges(wire.GetElement("changes")),
            Tool = wire.GetStrictString("tool"),
            SenderThreadId = wire.GetStrictString("sender_thread_id"),
            ReceiverThreadIds = wire.GetStrictStringList("receiver_thread_ids"),
        };
    }

    private static List<FileChange> ParseChanges(JsonElement? changes)
    {
        if (changes is null)
        {
            return [];
        }
        if (changes is not { ValueKind: JsonValueKind.Array } array)
        {
            throw new FormatException("field changes: expected array");
        }
        var parsed = new List<FileChange>(array.GetArrayLength());
        foreach (var change in array.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("field changes: expected object entries");
            }
            parsed.Add(new FileChange(change.GetStrictString("path"), change.GetStrictString("kind")));
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
        var message = wire.ValueKind == JsonValueKind.Object ? wire.GetStrictString("message") : "";
        if (message.Length == 0)
        {
            message = raw;
        }
        return new ErrorPayload { Message = message, Raw = raw };
    }

    private static ErrorPayload ParseTopLevelError(JsonElement root)
    {
        var raw = root.GetElement("error")?.GetRawText() ?? "";
        var message = root.GetStrictString("message");
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
        var parsed = new Usage
        {
            InputTokens = wire.GetStrictLong("input_tokens"),
            CachedInputTokens = wire.GetStrictLong("cached_input_tokens"),
            CacheCreationInputTokens = wire.GetStrictLong("cache_creation_input_tokens"),
            OutputTokens = wire.GetStrictLong("output_tokens"),
            ReasoningOutputTokens = wire.GetStrictLong("reasoning_output_tokens"),
            TotalTokens = wire.GetStrictLong("total_tokens"),
            TotalCostUsd = wire.GetStrictDouble("total_cost_usd"),
            ModelUsage = ParseModelUsage(wire.GetObject("model_usage")),
        };
        // Codex omits total_tokens on the current wire; cached input is a
        // subset of input, so the derived total is input plus output.
        if (parsed.TotalTokens == 0)
        {
            parsed = parsed with { TotalTokens = parsed.InputTokens + parsed.OutputTokens };
        }
        return parsed;
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
                InputTokens = model.GetStrictLong("input_tokens"),
                OutputTokens = model.GetStrictLong("output_tokens"),
                CacheReadInputTokens = model.GetStrictLong("cache_read_input_tokens"),
                CacheCreationInputTokens = model.GetStrictLong("cache_creation_input_tokens"),
                WebSearchRequests = model.GetStrictLong("web_search_requests"),
                CostUsd = model.GetStrictDouble("cost_usd"),
                ContextWindow = model.GetStrictLong("context_window"),
                MaxOutputTokens = model.GetStrictLong("max_output_tokens"),
            };
        }
        return parsed;
    }
}
