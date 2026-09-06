using System;
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

        return failures == 0 ? 0 : 1;
    }
}
