using System.Text.Json;

namespace C3OSS.Codexcw;

/// <summary>
/// Normalizes the claude -p stream-json events into the shared Event model.
/// Raw always keeps the original Claude JSON line.
/// </summary>
internal sealed class ClaudeDecoder : IEventDecoder
{
    private readonly Dictionary<string, Item> _pending = [];
    private readonly Dictionary<string, ulong> _blockSequences = [];
    private string _lastAgentText = "";

    public IReadOnlyList<Event> Decode(string line, string runId, string threadId, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.GetString("type");
        if (type.Length == 0)
        {
            throw new FormatException("missing event type");
        }

        var sessionId = root.GetString("session_id");
        var baseEvent = new Event
        {
            Kind = EventKind.Other,
            Type = type,
            RunId = runId,
            ThreadId = sessionId.Length > 0 ? sessionId : threadId,
            ReceivedAt = now,
            Raw = line,
        };

        return type switch
        {
            "system" => DecodeSystem(baseEvent, root, sessionId),
            "assistant" => DecodeAssistant(baseEvent, root),
            "user" => DecodeUser(baseEvent, root),
            "result" => DecodeResult(baseEvent, root),
            _ => [baseEvent],
        };
    }

    private static IReadOnlyList<Event> DecodeSystem(Event baseEvent, JsonElement root, string sessionId)
    {
        if (root.GetString("subtype") != "init")
        {
            return [baseEvent];
        }
        return
        [
            baseEvent with
            {
                Kind = EventKind.ThreadStarted,
                Type = EventTypes.ThreadStarted,
                ThreadStarted = new ThreadStartedPayload(sessionId),
            },
            baseEvent with
            {
                Kind = EventKind.TurnStarted,
                Type = EventTypes.TurnStarted,
            },
        ];
    }

    private List<Event> DecodeAssistant(Event baseEvent, JsonElement root)
    {
        var events = new List<Event>();
        var messageId = root.GetObject("message")?.GetString("id") ?? "";
        foreach (var block in ContentBlocks(root))
        {
            switch (block.GetString("type"))
            {
                case "text":
                    _lastAgentText = block.GetString("text");
                    events.Add(ItemCompleted(baseEvent, new Item
                    {
                        Id = NextBlockId(messageId),
                        Kind = ItemKind.AgentMessage,
                        Type = ItemTypes.AgentMessage,
                        Status = "completed",
                        Raw = block.GetRawText(),
                        Text = block.GetString("text"),
                    }));
                    break;
                case "thinking":
                    events.Add(ItemCompleted(baseEvent, new Item
                    {
                        Id = NextBlockId(messageId),
                        Kind = ItemKind.Reasoning,
                        Type = ItemTypes.Reasoning,
                        Status = "completed",
                        Raw = block.GetRawText(),
                        Text = block.GetString("thinking"),
                    }));
                    break;
                case "tool_use":
                    var item = ToolItem(block);
                    _pending[block.GetString("id")] = item;
                    events.Add(baseEvent with
                    {
                        Kind = EventKind.ItemStarted,
                        Type = EventTypes.ItemStarted,
                        ItemStarted = new ItemPayload(item),
                    });
                    break;
                default:
                    break;
            }
        }
        return events.Count > 0 ? events : [baseEvent];
    }

    private List<Event> DecodeUser(Event baseEvent, JsonElement root)
    {
        var events = new List<Event>();
        var toolUseResult = root.GetElement("tool_use_result");
        foreach (var block in ContentBlocks(root))
        {
            if (block.GetString("type") != "tool_result")
            {
                continue;
            }
            if (!_pending.Remove(block.GetString("tool_use_id"), out var item))
            {
                continue;
            }

            var isError = block.GetBool("is_error");
            var content = block.GetElement("content");
            item = item with
            {
                Raw = block.GetRawText(),
                AggregatedOutput = ToolResultText(content),
                Status = isError ? "failed" : "completed",
            };
            if (item.Kind == ItemKind.CommandExecution)
            {
                var exitCode = CommandExitCode(content, toolUseResult);
                if (exitCode is null && !isError)
                {
                    exitCode = 0;
                }
                item = item with { ExitCode = exitCode };
            }
            if (item.Kind == ItemKind.FileChange && item.Changes.Count > 0)
            {
                var kind = FileChangeKind(toolUseResult);
                if (kind.Length > 0)
                {
                    var changes = item.Changes.ToList();
                    changes[0] = changes[0] with { Kind = kind };
                    item = item with { Changes = changes };
                }
            }
            events.Add(ItemCompleted(baseEvent, item));
        }
        return events.Count > 0 ? events : [baseEvent];
    }

    private List<Event> DecodeResult(Event baseEvent, JsonElement root)
    {
        var usage = ResultUsage(root);
        if (root.GetBool("is_error"))
        {
            var message = root.GetString("result");
            if (message.Length == 0)
            {
                message = "claude run failed";
            }
            return
            [
                baseEvent with
                {
                    Kind = EventKind.TurnFailed,
                    Type = EventTypes.TurnFailed,
                    TurnFailed = new TurnFailedPayload(
                        new ErrorPayload { Message = message, Raw = baseEvent.Raw },
                        usage),
                },
            ];
        }

        var events = new List<Event>();
        var result = root.GetString("result");
        if (result.Length > 0 && result != _lastAgentText)
        {
            events.Add(ItemCompleted(baseEvent, new Item
            {
                Id = "result",
                Kind = ItemKind.AgentMessage,
                Type = ItemTypes.AgentMessage,
                Status = "completed",
                Raw = baseEvent.Raw,
                Text = result,
            }));
        }
        events.Add(baseEvent with
        {
            Kind = EventKind.TurnCompleted,
            Type = EventTypes.TurnCompleted,
            TurnCompleted = new TurnCompletedPayload(usage),
        });
        return events;
    }

    private static Usage ResultUsage(JsonElement root)
    {
        var wire = root.GetObject("usage");
        var inputTokens = wire?.GetLong("input_tokens") ?? 0;
        var cacheCreation = wire?.GetLong("cache_creation_input_tokens") ?? 0;
        var cacheRead = wire?.GetLong("cache_read_input_tokens") ?? 0;
        var outputTokens = wire?.GetLong("output_tokens") ?? 0;

        var modelUsage = new Dictionary<string, ModelUsage>();
        if (root.GetObject("modelUsage") is { } models)
        {
            foreach (var entry in models.EnumerateObject())
            {
                var model = entry.Value;
                modelUsage[entry.Name] = new ModelUsage
                {
                    InputTokens = model.GetLong("inputTokens"),
                    OutputTokens = model.GetLong("outputTokens"),
                    CacheReadInputTokens = model.GetLong("cacheReadInputTokens"),
                    CacheCreationInputTokens = model.GetLong("cacheCreationInputTokens"),
                    WebSearchRequests = model.GetLong("webSearchRequests"),
                    CostUsd = model.GetDouble("costUSD"),
                    ContextWindow = model.GetLong("contextWindow"),
                    MaxOutputTokens = model.GetLong("maxOutputTokens"),
                };
            }
        }

        return new Usage
        {
            InputTokens = inputTokens,
            CachedInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreation,
            OutputTokens = outputTokens,
            TotalTokens = inputTokens + cacheCreation + cacheRead + outputTokens,
            TotalCostUsd = root.GetDouble("total_cost_usd"),
            ModelUsage = modelUsage,
        };
    }

    private static Item ToolItem(JsonElement block)
    {
        var name = block.GetString("name");
        var input = block.GetObject("input");
        var item = new Item
        {
            Id = block.GetString("id"),
            Status = "in_progress",
            Raw = block.GetRawText(),
        };

        switch (name)
        {
            case "Bash":
                return item with
                {
                    Kind = ItemKind.CommandExecution,
                    Type = ItemTypes.CommandExecution,
                    Command = input?.GetString("command") ?? "",
                };
            case "Write" or "Edit" or "MultiEdit" or "NotebookEdit":
                var path = input?.GetString("file_path") ?? "";
                if (path.Length == 0)
                {
                    path = input?.GetString("notebook_path") ?? "";
                }
                return item with
                {
                    Kind = ItemKind.FileChange,
                    Type = ItemTypes.FileChange,
                    Changes = [new FileChange(path, name == "Write" ? "add" : "update")],
                };
            default:
                if (name.StartsWith("mcp__", StringComparison.Ordinal))
                {
                    return item with { Kind = ItemKind.McpToolCall, Type = ItemTypes.McpToolCall };
                }
                return name switch
                {
                    "WebSearch" => item with { Kind = ItemKind.WebSearch, Type = ItemTypes.WebSearch },
                    "Task" => item with { Kind = ItemKind.CollabToolCall, Type = ItemTypes.CollabToolCall },
                    "TodoWrite" => item with { Kind = ItemKind.PlanUpdate, Type = ItemTypes.PlanUpdate },
                    _ => item with { Kind = ItemKind.ToolCall, Type = ItemTypes.ToolCall },
                };
        }
    }

    private static IEnumerable<JsonElement> ContentBlocks(JsonElement root)
    {
        if (root.GetObject("message")?.GetElement("content") is not { ValueKind: JsonValueKind.Array } content)
        {
            yield break;
        }
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object)
            {
                yield return block;
            }
        }
    }

    private static string ToolResultText(JsonElement? content)
    {
        switch (content)
        {
            case { ValueKind: JsonValueKind.String } text:
                return text.GetString() ?? "";
            case { ValueKind: JsonValueKind.Array } blocks:
                var parts = new List<string>();
                foreach (var block in blocks.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object &&
                        block.GetString("type") == "text" &&
                        block.GetString("text") is { Length: > 0 } part)
                    {
                        parts.Add(part);
                    }
                }
                return string.Join('\n', parts);
            default:
                return "";
        }
    }

    private static string FileChangeKind(JsonElement? toolUseResult) =>
        toolUseResult is { ValueKind: JsonValueKind.Object } result
            ? result.GetString("type") switch
            {
                "create" => "add",
                "update" => "update",
                _ => "",
            }
            : "";

    internal static int? CommandExitCode(JsonElement? content, JsonElement? toolUseResult)
    {
        foreach (var candidate in new[] { toolUseResult, content })
        {
            if (candidate is not { } raw)
            {
                continue;
            }
            if (raw.ValueKind == JsonValueKind.Object)
            {
                if (raw.GetElement("exit_code") is { ValueKind: JsonValueKind.Number } snake)
                {
                    return snake.GetInt32();
                }
                if (raw.GetElement("exitCode") is { ValueKind: JsonValueKind.Number } camel)
                {
                    return camel.GetInt32();
                }
            }

            var text = raw.ValueKind == JsonValueKind.String
                ? raw.GetString() ?? ""
                : ToolResultText(raw);
            if (ExitCodeFromText(text) is { } exitCode)
            {
                return exitCode;
            }
        }
        return null;
    }

    internal static int? ExitCodeFromText(string text)
    {
        const string marker = "exit code ";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index == -1)
        {
            return null;
        }
        var value = text[(index + marker.Length)..];
        var end = 0;
        while (end < value.Length && (value[end] == '-' || (value[end] >= '0' && value[end] <= '9')))
        {
            end++;
        }
        if (end == 0)
        {
            return null;
        }
        return int.TryParse(value[..end], out var code) ? code : null;
    }

    private string NextBlockId(string messageId)
    {
        _blockSequences.TryGetValue(messageId, out var sequence);
        _blockSequences[messageId] = sequence + 1;
        return messageId.Length == 0 ? $"block_{sequence}" : $"{messageId}_{sequence}";
    }

    private static Event ItemCompleted(Event baseEvent, Item item) => baseEvent with
    {
        Kind = EventKind.ItemCompleted,
        Type = EventTypes.ItemCompleted,
        ItemCompleted = new ItemPayload(item),
    };
}
