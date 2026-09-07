using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static class Program
{
    private sealed class TestCase
    {
        public string Name;
        public string Json;
        public string Expectation;
        public bool Valid;
        public string ReasonPrefix;
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: RpgProtocolSmokeTests <RimChat.dll> <RimWorld managed directory>");
            return 2;
        }

        string rimChatAssemblyPath = Path.GetFullPath(args[0]);
        string managedDirectory = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            string name = new AssemblyName(eventArgs.Name).Name + ".dll";
            foreach (string directory in new[] { Path.GetDirectoryName(rimChatAssemblyPath), managedDirectory })
            {
                string candidate = Path.Combine(directory ?? string.Empty, name);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };

        Assembly rimChat = Assembly.LoadFrom(rimChatAssemblyPath);
        Type parserType = rimChat.GetType("RimChat.Dialogue.DialogueResponseEnvelopeParser", true);
        Type channelType = rimChat.GetType("RimChat.AI.DialogueUsageChannel", true);
        Type expectationType = rimChat.GetType("RimChat.Dialogue.DialogueResponseExpectation", true);
        MethodInfo parse = parserType.GetMethod("Parse", new[] { typeof(string), channelType, expectationType });
        object rpgChannel = Enum.Parse(channelType, "Rpg");

        var cases = new List<TestCase>
        {
            new TestCase
            {
                Name = "topics_accept_no_graph",
                Expectation = "RpgTopics",
                Valid = true,
                Json = "{\"visible_dialogue\":\"hello\",\"topics\":[{\"id\":\"t1\",\"text\":\"one\"},{\"id\":\"t2\",\"text\":\"two\"}],\"choices\":[]}"
            },
            new TestCase
            {
                Name = "graph_reject_missing",
                Expectation = "RpgScriptGraph",
                Valid = false,
                ReasonPrefix = "missing_script_graph",
                Json = "{\"visible_dialogue\":\"only one line\",\"topics\":[],\"choices\":[]}"
            },
            new TestCase
            {
                Name = "graph_accept_valid",
                Expectation = "RpgScriptGraph",
                Valid = true,
                Json = "{\"visible_dialogue\":\"start\",\"topics\":[],\"choices\":[],\"start_node_id\":\"n1\",\"script_nodes\":[{\"id\":\"n1\",\"speaker_alias\":\"npc_1\",\"dialogue\":\"start\",\"choices\":[{\"id\":\"c1\",\"text\":\"ask\",\"spoken_text\":\"Could you tell me more?\",\"success\":{\"effects\":[],\"end_mode\":\"normal\",\"next_node_id\":\"n2\"},\"failure\":{\"effects\":[],\"end_mode\":\"normal\",\"next_node_id\":\"n2\"}}]},{\"id\":\"n2\",\"speaker_alias\":\"npc_1\",\"dialogue\":\"end\",\"choices\":[]}]}"
            },
            new TestCase
            {
                Name = "graph_reject_invalid_edge",
                Expectation = "RpgScriptGraph",
                Valid = false,
                ReasonPrefix = "invalid_script_graph",
                Json = "{\"visible_dialogue\":\"start\",\"topics\":[],\"choices\":[],\"start_node_id\":\"n1\",\"script_nodes\":[{\"id\":\"n1\",\"speaker_alias\":\"npc_1\",\"dialogue\":\"start\",\"choices\":[{\"id\":\"c1\",\"text\":\"ask\",\"spoken_text\":\"Could you tell me more?\",\"success\":{\"effects\":[],\"end_mode\":\"none\",\"next_node_id\":\"missing\"},\"failure\":{\"effects\":[],\"end_mode\":\"none\",\"next_node_id\":\"missing\"}}]}]}"
            },
            new TestCase
            {
                Name = "default_accepts_visible_only",
                Expectation = "Default",
                Valid = true,
                Json = "{\"visible_dialogue\":\"ordinary\"}"
            }
        };

        int failures = 0;
        foreach (TestCase test in cases)
        {
            object expectation = Enum.Parse(expectationType, test.Expectation);
            object envelope = parse.Invoke(null, new[] { test.Json, rpgChannel, expectation });
            Type envelopeType = envelope.GetType();
            bool actualValid = (bool)envelopeType.GetProperty("IsValid").GetValue(envelope, null);
            string reason = (string)envelopeType.GetProperty("FailureReason").GetValue(envelope, null) ?? string.Empty;
            bool pass = actualValid == test.Valid &&
                (string.IsNullOrEmpty(test.ReasonPrefix) || reason.StartsWith(test.ReasonPrefix, StringComparison.Ordinal));
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {test.Name}: valid={actualValid}, reason={reason}");
            if (!pass) failures++;
        }

        failures += RunNativeToolProtocolTests(rimChat);
        failures += RunNativeToolSchemaTests(rimChat);
        failures += RunCustomConfigSupplementationTests(rimChat);
        failures += RunAirdropTradeCardStateTests(rimChat);

        return failures == 0 ? 0 : 1;
    }

    private static int RunNativeToolProtocolTests(Assembly rimChat)
    {
        int failures = 0;
        Type parserType = rimChat.GetType("RimChat.AI.NativeChatCompletionParser", true);
        MethodInfo parse = parserType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);

        failures += CheckNativeTurn(
            parse,
            "native_final_text",
            "{\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"成交。\"}}]}",
            true,
            false,
            string.Empty);
        failures += CheckNativeTurn(
            parse,
            "native_tool_call",
            "{\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"accept_item_airdrop\",\"arguments\":\"{\\\"request_id\\\":\\\"abc123\\\"}\"}}]}}]}",
            true,
            true,
            string.Empty);
        failures += CheckNativeTurn(parse, "native_invalid_provider_json", "not-json", false, false, "invalid_provider_json");
        failures += CheckNativeTurn(
            parse,
            "native_duplicate_tool_id",
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"same\",\"type\":\"function\",\"function\":{\"name\":\"request_visitor\",\"arguments\":\"{}\"}},{\"id\":\"same\",\"type\":\"function\",\"function\":{\"name\":\"request_visitor\",\"arguments\":\"{}\"}}]}}]}",
            false,
            false,
            "duplicate_tool_call_id");

        failures += CheckToolArguments(rimChat, "accept_args_valid", "accept_item_airdrop", "{\"request_id\":\"abc123\"}", true);
        failures += CheckToolArguments(rimChat, "accept_args_missing_id", "accept_item_airdrop", "{}", false);
        failures += CheckToolArguments(rimChat, "accept_args_rejects_modified_terms", "accept_item_airdrop", "{\"request_id\":\"abc123\",\"price\":1}", false);
        failures += CheckToolArguments(
            rimChat,
            "multi_airdrop_args_valid",
            "request_item_airdrop",
            "{\"need_items\":[{\"item\":\"Steel\",\"count\":20},{\"item\":\"WoodLog\",\"count\":10}],\"payment_items\":[{\"item\":\"Silver\",\"count\":200}]}",
            true);
        failures += CheckToolArguments(
            rimChat,
            "nullable_optional_arg_normalized",
            "request_item_airdrop",
            "{\"need_items\":[{\"item\":\"Steel\",\"count\":20}],\"payment_items\":[{\"item\":\"Silver\",\"count\":200}],\"scenario\":null}",
            true);
        failures += CheckToolArguments(
            rimChat,
            "multi_airdrop_rejects_fractional_count",
            "request_item_airdrop",
            "{\"need_items\":[{\"item\":\"Steel\",\"count\":1.5}],\"payment_items\":[{\"item\":\"Silver\",\"count\":200}]}",
            false);
        failures += CheckToolArguments(
            rimChat,
            "enum_values_require_canonical_case",
            "request_aid",
            "{\"type\":\"military\"}",
            false);
        return failures;
    }

    private static int RunAirdropTradeCardStateTests(Assembly rimChat)
    {
        Type sessionType = rimChat.GetType("RimChat.Memory.FactionDialogueSession", true);
        Type statusType = rimChat.GetType("RimChat.Memory.AirdropTradeCardStatus", true);
        object session = Activator.CreateInstance(sessionType);
        string requestId = "trade_card_1";
        object pending = Enum.Parse(statusType, "Pending");
        object awaiting = Enum.Parse(statusType, "AwaitingPlayerConfirm");
        object cancelled = Enum.Parse(statusType, "Cancelled");
        object superseded = Enum.Parse(statusType, "Superseded");

        sessionType.GetField("hasPendingAirdropTradeCardReference").SetValue(session, true);
        sessionType.GetField("pendingAirdropTradeCardRequestId").SetValue(session, requestId);
        sessionType.GetField("pendingAirdropTradeCardStatus").SetValue(session, pending);
        object statusMap = sessionType.GetField("airdropTradeCardStatusByRequestId").GetValue(session);
        statusMap.GetType().GetProperty("Item").SetValue(statusMap, pending, new object[] { requestId });

        MethodInfo setStatus = sessionType.GetMethod("SetAirdropTradeCardStatus", BindingFlags.Public | BindingFlags.Instance);
        setStatus.Invoke(session, new[] { requestId, awaiting });
        setStatus.Invoke(session, new[] { requestId, pending });
        object recovered = sessionType.GetField("pendingAirdropTradeCardStatus").GetValue(session);
        bool recoveryPass = string.Equals(recovered.ToString(), "Pending", StringComparison.Ordinal);
        Console.WriteLine($"{(recoveryPass ? "PASS" : "FAIL")} airdrop_uncommitted_can_return_pending: status={recovered}");

        setStatus.Invoke(session, new[] { requestId, cancelled });
        setStatus.Invoke(session, new[] { requestId, pending });
        object terminal = sessionType.GetField("pendingAirdropTradeCardStatus").GetValue(session);
        bool terminalPass = string.Equals(terminal.ToString(), "Cancelled", StringComparison.Ordinal);
        Console.WriteLine($"{(terminalPass ? "PASS" : "FAIL")} airdrop_terminal_status_is_immutable: status={terminal}");

        string oldRequestId = "trade_card_old";
        statusMap.GetType().GetProperty("Item").SetValue(statusMap, pending, new object[] { oldRequestId });
        setStatus.Invoke(session, new[] { oldRequestId, superseded });
        setStatus.Invoke(session, new[] { oldRequestId, pending });
        object supersededStatus = statusMap.GetType().GetProperty("Item").GetValue(statusMap, new object[] { oldRequestId });
        bool supersededPass = string.Equals(supersededStatus.ToString(), "Superseded", StringComparison.Ordinal);
        Console.WriteLine($"{(supersededPass ? "PASS" : "FAIL")} airdrop_superseded_id_is_immutable: status={supersededStatus}");
        return (recoveryPass ? 0 : 1) + (terminalPass ? 0 : 1) + (supersededPass ? 0 : 1);
    }

    private static int RunNativeToolSchemaTests(Assembly rimChat)
    {
        Type catalogType = rimChat.GetType("RimChat.AI.NativeToolCatalog", true);
        FieldInfo schemasField = catalogType.GetField("Schemas", BindingFlags.NonPublic | BindingFlags.Static);
        var schemas = (IDictionary<string, string>)schemasField.GetValue(null);
        Type jsonType = rimChat.GetType("RimChat.Persistence.ReflectionJsonFieldDeserializer", true);
        MethodInfo parseUntyped = jsonType.GetMethod("TryParseUntyped", BindingFlags.NonPublic | BindingFlags.Static);
        int failures = 0;

        foreach (KeyValuePair<string, string> schema in schemas)
        {
            object[] parseArgs = { schema.Value, null };
            bool parsed = (bool)parseUntyped.Invoke(null, parseArgs);
            string reason = parsed && parseArgs[1] is Dictionary<string, object> root
                ? FindStrictSchemaError(root, schema.Key)
                : "schema is not valid JSON";
            bool pass = string.IsNullOrEmpty(reason);
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} strict_schema_{schema.Key}: {reason}");
            if (!pass) failures++;
        }

        return failures;
    }

    private static int RunCustomConfigSupplementationTests(Assembly rimChat)
    {
        Type domainType = rimChat.GetType("RimChat.Persistence.DiplomacyDialoguePromptDomainConfig", true);
        Type actionType = rimChat.GetType("RimChat.Config.ApiActionConfig", true);
        Type serviceType = rimChat.GetType("RimChat.Persistence.PromptPersistenceService", true);
        object domain = Activator.CreateInstance(domainType);
        IList actions = (IList)domainType.GetField("ApiActions").GetValue(domain);
        object existing = Activator.CreateInstance(actionType);
        actionType.GetField("ActionName").SetValue(existing, "request_item_airdrop");
        actionType.GetField("Description").SetValue(existing, "keep-my-custom-description");
        actionType.GetField("IsEnabled").SetValue(existing, true);
        actions.Add(existing);

        MethodInfo build = serviceType.GetMethod("BuildApiActions", BindingFlags.NonPublic | BindingFlags.Static);
        IList supplemented = (IList)build.Invoke(null, new[] { domain });
        bool hasAccept = false;
        bool preservedExisting = false;
        foreach (object action in supplemented)
        {
            string name = (string)actionType.GetField("ActionName").GetValue(action);
            string description = (string)actionType.GetField("Description").GetValue(action);
            if (string.Equals(name, "accept_item_airdrop", StringComparison.Ordinal)) hasAccept = true;
            if (string.Equals(name, "request_item_airdrop", StringComparison.Ordinal) &&
                string.Equals(description, "keep-my-custom-description", StringComparison.Ordinal)) preservedExisting = true;
        }

        bool pass = hasAccept && preservedExisting;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} custom_config_supplements_accept_tool: hasAccept={hasAccept}, preservedExisting={preservedExisting}");
        return pass ? 0 : 1;
    }

    private static string FindStrictSchemaError(Dictionary<string, object> schema, string path)
    {
        bool isObject = schema.TryGetValue("type", out object type) &&
            (string.Equals(type as string, "object", StringComparison.Ordinal) ||
             type is List<object> types && types.Exists(value => string.Equals(value as string, "object", StringComparison.Ordinal)));
        if (isObject)
        {
            if (!(schema.TryGetValue("additionalProperties", out object additional) && additional is bool allowed && !allowed))
            {
                return path + " must set additionalProperties=false";
            }
            if (!(schema.TryGetValue("properties", out object rawProperties) && rawProperties is Dictionary<string, object> properties))
            {
                return path + " must define properties";
            }
            if (!(schema.TryGetValue("required", out object rawRequired) && rawRequired is List<object> required))
            {
                return path + " must define required";
            }
            var requiredNames = new HashSet<string>();
            foreach (object item in required)
            {
                if (item is string name) requiredNames.Add(name);
            }
            if (!requiredNames.SetEquals(properties.Keys))
            {
                return path + " required must contain every property exactly once";
            }
            foreach (KeyValuePair<string, object> property in properties)
            {
                if (property.Value is Dictionary<string, object> child)
                {
                    string childError = FindStrictSchemaError(child, path + "." + property.Key);
                    if (!string.IsNullOrEmpty(childError)) return childError;
                }
            }
        }

        if (schema.TryGetValue("items", out object rawItems) && rawItems is Dictionary<string, object> items)
        {
            return FindStrictSchemaError(items, path + "[]");
        }
        return string.Empty;
    }

    private static int CheckNativeTurn(
        MethodInfo parse,
        string name,
        string json,
        bool expectedValid,
        bool expectedToolCalls,
        string expectedErrorCode)
    {
        object turn = parse.Invoke(null, new object[] { json });
        Type turnType = turn.GetType();
        bool valid = (bool)turnType.GetProperty("IsValid").GetValue(turn, null);
        bool hasToolCalls = (bool)turnType.GetProperty("HasToolCalls").GetValue(turn, null);
        string errorCode = (string)turnType.GetProperty("ErrorCode").GetValue(turn, null) ?? string.Empty;
        bool pass = valid == expectedValid &&
            hasToolCalls == expectedToolCalls &&
            (string.IsNullOrEmpty(expectedErrorCode) || string.Equals(errorCode, expectedErrorCode, StringComparison.Ordinal));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: valid={valid}, tools={hasToolCalls}, error={errorCode}");
        return pass ? 0 : 1;
    }

    private static int CheckToolArguments(
        Assembly rimChat,
        string name,
        string toolName,
        string json,
        bool expectedValid)
    {
        Type argumentParserType = rimChat.GetType("RimChat.AI.NativeToolArgumentParser", true);
        MethodInfo parseObject = argumentParserType.GetMethod("TryParseObject", BindingFlags.Public | BindingFlags.Static);
        object[] parseArgs = { json, null, null };
        bool parsed = (bool)parseObject.Invoke(null, parseArgs);
        bool valid = false;
        string errorCode = parsed ? string.Empty : (string)parseArgs[2] ?? string.Empty;
        if (parsed)
        {
            Type catalogType = rimChat.GetType("RimChat.AI.NativeToolCatalog", true);
            MethodInfo validate = catalogType.GetMethod("TryValidateAndNormalize", BindingFlags.Public | BindingFlags.Static);
            object[] validateArgs = { toolName, parseArgs[1], null, null, null };
            valid = (bool)validate.Invoke(null, validateArgs);
            errorCode = (string)validateArgs[3] ?? string.Empty;
        }

        bool pass = valid == expectedValid;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: valid={valid}, error={errorCode}");
        return pass ? 0 : 1;
    }
}
