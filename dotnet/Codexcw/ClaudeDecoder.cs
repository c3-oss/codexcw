using System.Text;
using System.Text.Json;

namespace C3OSS.Codexcw;

/// <summary>
/// Normalizes Messages-compatible JSONL into the shared Event model.
/// Raw always keeps the original agent JSON line.
/// </summary>
internal sealed class ClaudeDecoder : IEventDecoder
{
    private readonly Agent _agent;
    private readonly Dictionary<string, Item> _pending = [];
    private readonly Dictionary<string, ulong> _blockSequences = [];
    private string _lastAgentText = "";

    public ClaudeDecoder(Agent agent = Agent.Claude)
    {
        _agent = agent;
    }

    public IReadOnlyList<Event> Decode(string line, string runId, string threadId, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.GetStrictString("type");
        if (type.Length == 0)
        {
            throw new FormatException("missing event type");
        }

        var sessionId = root.GetStrictString("session_id");
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
        if (root.GetStrictString("subtype") != "init")
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
                TurnStarted = new TurnStartedPayload(),
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
                case "server_tool_use":
                    var item = ToolItem(block);
                    _pending[block.GetString("id")] = item;
                    events.Add(baseEvent with
                    {
                        Kind = EventKind.ItemStarted,
                        Type = EventTypes.ItemStarted,
                        ItemStarted = new ItemPayload(item),
                    });
                    break;
                case "web_search_tool_result":
                    if (!_pending.Remove(block.GetString("tool_use_id"), out var webItem))
                    {
                        break;
                    }
                    var webError = block.GetBool("is_error");
                    events.Add(ItemCompleted(baseEvent, webItem with
                    {
                        Raw = block.GetRawText(),
                        AggregatedOutput = ToolResultText(block.GetElement("content")),
                        Status = webError ? "failed" : "completed",
                    }));
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
                var exitCode = CommandExitCode(content, toolUseResult, _agent);
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
            var collab = toolUseResult;
            if (collab is null && _agent == Agent.Grok)
            {
                collab = NestedGrokResult(content);
            }
            if (item.Kind == ItemKind.CollabToolCall &&
                collab is { ValueKind: JsonValueKind.Object } collabResult)
            {
                var agentId = new[]
                {
                    collabResult.GetString("agentId"),
                    collabResult.GetString("task_id"),
                    collabResult.GetString("subagent_id"),
                }.FirstOrDefault(static id => id.Length > 0);
                if (agentId is not null)
                {
                    item = item with { ReceiverThreadIds = [agentId] };
                }
            }
            events.Add(ItemCompleted(baseEvent, item));
        }
        return events.Count > 0 ? events : [baseEvent];
    }

    private List<Event> DecodeResult(Event baseEvent, JsonElement root)
    {
        var usage = ResultUsage(root);
        if (root.GetStrictBool("is_error"))
        {
            var message = root.GetStrictString("result");
            if (message.Length == 0 &&
                root.GetElement("errors") is { ValueKind: JsonValueKind.Array } errors)
            {
                message = string.Join("; ", errors.EnumerateArray()
                    .Where(static error => error.ValueKind == JsonValueKind.String)
                    .Select(static error => error.GetString()));
            }
            if (message.Length == 0)
            {
                message = _agent.Name() + " run failed";
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
        var result = root.GetStrictString("result");
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
        var inputTokens = wire?.GetStrictLong("input_tokens") ?? 0;
        var cacheCreation = wire?.GetStrictLong("cache_creation_input_tokens") ?? 0;
        var cacheRead = wire?.GetStrictLong("cache_read_input_tokens") ?? 0;
        var outputTokens = wire?.GetStrictLong("output_tokens") ?? 0;

        var modelUsage = new Dictionary<string, ModelUsage>();
        if (root.GetObject("modelUsage") is { } models)
        {
            foreach (var entry in models.EnumerateObject())
            {
                var model = entry.Value;
                modelUsage[entry.Name] = new ModelUsage
                {
                    InputTokens = model.GetStrictLong("inputTokens"),
                    OutputTokens = model.GetStrictLong("outputTokens"),
                    CacheReadInputTokens = model.GetStrictLong("cacheReadInputTokens"),
                    CacheCreationInputTokens = model.GetStrictLong("cacheCreationInputTokens"),
                    WebSearchRequests = model.GetStrictLong("webSearchRequests"),
                    CostUsd = model.GetStrictDouble("costUSD"),
                    ContextWindow = model.GetStrictLong("contextWindow"),
                    MaxOutputTokens = model.GetStrictLong("maxOutputTokens"),
                };
            }
        }

        // result.usage covers the root agent only; modelUsage and the cost
        // also cover subagents. The full-run total comes from modelUsage when
        // the agent reports it.
        var totalTokens = inputTokens + cacheCreation + cacheRead + outputTokens;
        if (modelUsage.Count > 0)
        {
            totalTokens = modelUsage.Values.Sum(static model =>
                model.InputTokens +
                model.CacheCreationInputTokens +
                model.CacheReadInputTokens +
                model.OutputTokens);
        }

        return new Usage
        {
            InputTokens = inputTokens,
            CachedInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreation,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens,
            TotalCostUsd = root.GetStrictDouble("total_cost_usd"),
            ModelUsage = modelUsage,
        };
    }

    private Item ToolItem(JsonElement block)
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
            case "Bash" or "run_terminal_command":
                return item with
                {
                    Kind = ItemKind.CommandExecution,
                    Type = ItemTypes.CommandExecution,
                    Command = input?.GetString("command") ?? "",
                };
            case "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or
                "search_replace" or "write":
                var path = input?.GetString("file_path") ?? "";
                if (path.Length == 0)
                {
                    path = input?.GetString("notebook_path") ?? "";
                }
                if (path.Length == 0)
                {
                    path = input?.GetString("target_file") ?? "";
                }
                if (path.Length == 0)
                {
                    path = input?.GetString("path") ?? "";
                }
                return item with
                {
                    Kind = ItemKind.FileChange,
                    Type = ItemTypes.FileChange,
                    Changes = [new FileChange(
                        path,
                        name is "Write" or "write" ? "add" : "update")],
                };
            default:
                if (name.StartsWith("mcp__", StringComparison.Ordinal) ||
                    (_agent == Agent.Grok && name == "use_tool"))
                {
                    return item with { Kind = ItemKind.McpToolCall, Type = ItemTypes.McpToolCall };
                }
                return name switch
                {
                    "WebSearch" or "web_search" or "web_fetch" => item with
                    {
                        Kind = ItemKind.WebSearch,
                        Type = ItemTypes.WebSearch,
                    },
                    // "Task" is the legacy name of the subagent tool; current
                    // Claude Code CLIs call it "Agent".
                    "Task" or "Agent" or "spawn_subagent" => item with
                    {
                        Kind = ItemKind.CollabToolCall,
                        Type = ItemTypes.CollabToolCall,
                        Tool = name,
                    },
                    "TodoWrite" or "todo_write" => item with
                    {
                        Kind = ItemKind.PlanUpdate,
                        Type = ItemTypes.PlanUpdate,
                    },
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

    private string ToolResultText(JsonElement? content)
    {
        switch (content)
        {
            case { ValueKind: JsonValueKind.String } text:
                var value = text.GetString() ?? "";
                if (_agent == Agent.Grok &&
                    NestedGrokResult(content) is { ValueKind: JsonValueKind.Object } result)
                {
                    if (result.GetElement("output") is
                        { ValueKind: JsonValueKind.Array } output)
                    {
                        var bytes = output.EnumerateArray()
                            .Where(static item => item.ValueKind == JsonValueKind.Number)
                            .Select(static item => item.GetByte())
                            .ToArray();
                        if (bytes.Length > 0)
                        {
                            return Encoding.UTF8.GetString(bytes);
                        }
                    }
                    if (result.GetString("output_for_prompt") is { Length: > 0 } promptOutput)
                    {
                        return promptOutput;
                    }
                }
                return value;
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

    internal static int? CommandExitCode(
        JsonElement? content,
        JsonElement? toolUseResult,
        Agent agent = Agent.Claude)
    {
        if (agent == Agent.Grok && NestedGrokResult(content) is { } grokResult)
        {
            toolUseResult = grokResult;
        }
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
                : RawToolResultText(raw);
            if (ExitCodeFromText(text) is { } exitCode)
            {
                return exitCode;
            }
        }
        return null;
    }

    private static JsonElement? NestedGrokResult(JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.String } text)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(text.GetString() ?? "");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RawToolResultText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                block.GetString("type") == "text" &&
                block.GetString("text") is { Length: > 0 } part)
            {
                parts.Add(part);
            }
        }
        return string.Join('\n', parts);
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
