using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimChat.Config;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using RimChat.Persistence;
using RimWorld;

namespace RimChat.AI
{
    internal static class NativeToolCatalog
    {
        private const int MaxAirdropItemRows = 32;
        private const int MaxAirdropItemCount = 1000000;
        private const string EmptyObject = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
        private const string ItemRows = "{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\",\"minLength\":1},\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000}},\"required\":[\"item\",\"count\"],\"additionalProperties\":false}}";

        private static readonly Dictionary<string, string> Schemas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AIActionNames.AdjustGoodwill] = Obj("\"amount\":{\"type\":\"integer\"},\"reason\":{\"type\":[\"string\",\"null\"]}", "\"amount\",\"reason\""),
            [AIActionNames.SendGift] = Obj("\"silver\":{\"type\":\"integer\",\"minimum\":1},\"goodwill_gain\":{\"type\":\"integer\",\"minimum\":0}", "\"silver\",\"goodwill_gain\""),
            [AIActionNames.RequestAid] = Obj("\"type\":{\"type\":\"string\",\"enum\":[\"Military\",\"Medical\",\"Resources\"]}", "\"type\""),
            [AIActionNames.DeclareWar] = Obj("\"reason\":{\"type\":[\"string\",\"null\"]}", "\"reason\""),
            [AIActionNames.MakePeace] = Obj("\"cost\":{\"type\":\"integer\",\"minimum\":1}", "\"cost\""),
            [AIActionNames.RequestCaravan] = Obj("\"type\":{\"type\":[\"string\",\"null\"],\"enum\":[\"General\",\"BulkGoods\",\"CombatSupplier\",\"Exotic\",\"Slaver\",null]}", "\"type\""),
            [AIActionNames.RequestVisitor] = EmptyObject,
            [AIActionNames.RequestRaid] = Obj("\"strategy\":{\"type\":[\"string\",\"null\"],\"enum\":[\"ImmediateAttack\",\"ImmediateAttackSmart\",\"StageThenAttack\",\"ImmediateAttackSappers\",\"Siege\",null]},\"arrival\":{\"type\":[\"string\",\"null\"],\"enum\":[\"EdgeWalkIn\",\"EdgeDrop\",\"EdgeWalkInGroups\",\"RandomDrop\",\"CenterDrop\",null]}", "\"strategy\",\"arrival\""),
            [AIActionNames.RequestRaidCallEveryone] = EmptyObject,
            [AIActionNames.RequestRaidWaves] = Obj("\"waves\":{\"type\":\"integer\",\"minimum\":2,\"maximum\":6}", "\"waves\""),
            [AIActionNames.RequestItemAirdrop] = Obj($"\"need_items\":{ItemRows},\"payment_items\":{ItemRows},\"scenario\":{{\"type\":[\"string\",\"null\"],\"enum\":[\"general\",\"trade\",\"ransom\",null]}}", "\"need_items\",\"payment_items\",\"scenario\""),
            [AIActionNames.AcceptItemAirdrop] = Obj("\"request_id\":{\"type\":\"string\",\"minLength\":1}", "\"request_id\""),
            [AIActionNames.RequestInfo] = Obj("\"info_type\":{\"type\":\"string\",\"enum\":[\"prisoner\"]}", "\"info_type\""),
            [AIActionNames.PayPrisonerRansom] = Obj("\"target_pawn_load_id\":{\"type\":\"integer\",\"minimum\":1},\"offer_silver\":{\"type\":\"integer\",\"minimum\":1},\"payment_mode\":{\"type\":[\"string\",\"null\"],\"enum\":[\"silver\",null]}", "\"target_pawn_load_id\",\"offer_silver\",\"payment_mode\""),
            [AIActionNames.TriggerIncident] = Obj("\"defName\":{\"type\":\"string\",\"minLength\":1},\"amount\":{\"type\":[\"integer\",\"null\"]}", "\"defName\",\"amount\""),
            [AIActionNames.CreateQuest] = Obj("\"questDefName\":{\"type\":\"string\",\"minLength\":1},\"askerFaction\":{\"type\":[\"string\",\"null\"]},\"points\":{\"type\":[\"integer\",\"null\"],\"minimum\":0}", "\"questDefName\",\"askerFaction\",\"points\""),
            [AIActionNames.SendImage] = Obj("\"template_id\":{\"type\":\"string\",\"minLength\":1},\"extra_prompt\":{\"type\":[\"string\",\"null\"]},\"caption\":{\"type\":[\"string\",\"null\"]},\"size\":{\"type\":[\"string\",\"null\"]},\"watermark\":{\"type\":[\"boolean\",\"null\"]}", "\"template_id\",\"extra_prompt\",\"caption\",\"size\",\"watermark\""),
            [AIActionNames.RejectRequest] = Obj("\"reason\":{\"type\":[\"string\",\"null\"]}", "\"reason\""),
            [AIActionNames.PublishPublicPost] = Obj("\"category\":{\"type\":\"string\",\"enum\":[\"Military\",\"Economic\",\"Diplomatic\",\"Anomaly\"]},\"sentiment\":{\"type\":\"integer\",\"minimum\":-2,\"maximum\":2},\"summary\":{\"type\":[\"string\",\"null\"]},\"targetFaction\":{\"type\":[\"string\",\"null\"]},\"intentHint\":{\"type\":[\"string\",\"null\"]}", "\"category\",\"sentiment\",\"summary\",\"targetFaction\",\"intentHint\""),
            [AIActionNames.ExitDialogue] = Obj("\"reason\":{\"type\":[\"string\",\"null\"]}", "\"reason\""),
            [AIActionNames.GoOffline] = Obj("\"reason\":{\"type\":[\"string\",\"null\"]}", "\"reason\""),
            [AIActionNames.SetDnd] = Obj("\"reason\":{\"type\":[\"string\",\"null\"]}", "\"reason\"")
        };

        public static List<NativeToolDefinition> Build(Faction faction, FactionDialogueSession session)
        {
            PromptPersistenceService.Instance.Initialize();
            SystemPromptConfig config = PromptPersistenceService.Instance.LoadConfig();
            Dictionary<string, ActionValidationResult> eligibility = faction == null
                ? new Dictionary<string, ActionValidationResult>(StringComparer.Ordinal)
                : ApiActionEligibilityService.Instance.GetAllowedActions(faction);

            var result = new List<NativeToolDefinition>();
            var addedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ApiActionConfig action in config?.ApiActions ?? new List<ApiActionConfig>())
            {
                string name = action?.ActionName?.Trim() ?? string.Empty;
                if (action?.IsEnabled != true || !Schemas.TryGetValue(name, out string schema) || !addedNames.Add(name))
                {
                    continue;
                }

                if (name == AIActionNames.AcceptItemAirdrop && session?.hasPendingAirdropTradeCardReference != true)
                {
                    continue;
                }
                if (name == AIActionNames.RequestItemAirdrop && session?.hasPendingAirdropTradeCardReference == true)
                {
                    continue;
                }

                if (eligibility.TryGetValue(name, out ActionValidationResult allowed) && !allowed.Allowed)
                {
                    continue;
                }

                result.Add(new NativeToolDefinition
                {
                    Name = name,
                    Description = BuildDescription(name, action),
                    ParametersJson = schema
                });
            }
            return result;
        }

        public static bool IsKnown(string name) => !string.IsNullOrWhiteSpace(name) && Schemas.ContainsKey(name);

        public static bool TryValidateAndNormalize(
            string name,
            Dictionary<string, object> parameters,
            out Dictionary<string, object> normalized,
            out string errorCode,
            out string errorMessage)
        {
            normalized = parameters == null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : new Dictionary<string, object>(parameters, StringComparer.Ordinal);
            errorCode = string.Empty;
            errorMessage = string.Empty;

            HashSet<string> allowed = AllowedKeys(name);
            if (allowed == null)
            {
                return Fail("unknown_tool", $"Unknown tool: {name}", out errorCode, out errorMessage);
            }

            string extra = normalized.Keys.FirstOrDefault(key => !allowed.Contains(key));
            if (!string.IsNullOrWhiteSpace(extra))
            {
                return Fail("invalid_arguments", $"Unexpected parameter '{extra}' for {name}.", out errorCode, out errorMessage);
            }

            switch (name)
            {
                case AIActionNames.AdjustGoodwill:
                    return RequireInt(normalized, "amount", int.MinValue, int.MaxValue, out errorCode, out errorMessage) && OptionalString(normalized, "reason", out errorCode, out errorMessage);
                case AIActionNames.SendGift:
                    return RequireInt(normalized, "silver", 1, int.MaxValue, out errorCode, out errorMessage) && RequireInt(normalized, "goodwill_gain", 0, int.MaxValue, out errorCode, out errorMessage);
                case AIActionNames.RequestAid:
                    return RequireEnum(normalized, "type", new[] { "Military", "Medical", "Resources" }, out errorCode, out errorMessage);
                case AIActionNames.DeclareWar:
                case AIActionNames.RejectRequest:
                case AIActionNames.ExitDialogue:
                case AIActionNames.GoOffline:
                case AIActionNames.SetDnd:
                    return OptionalString(normalized, "reason", out errorCode, out errorMessage);
                case AIActionNames.MakePeace:
                    return RequireInt(normalized, "cost", 1, int.MaxValue, out errorCode, out errorMessage);
                case AIActionNames.RequestCaravan:
                    return OptionalEnum(normalized, "type", new[] { "General", "BulkGoods", "CombatSupplier", "Exotic", "Slaver" }, out errorCode, out errorMessage);
                case AIActionNames.RequestVisitor:
                case AIActionNames.RequestRaidCallEveryone:
                    return true;
                case AIActionNames.RequestRaid:
                    return OptionalEnum(normalized, "strategy", new[] { "ImmediateAttack", "ImmediateAttackSmart", "StageThenAttack", "ImmediateAttackSappers", "Siege" }, out errorCode, out errorMessage) &&
                           OptionalEnum(normalized, "arrival", new[] { "EdgeWalkIn", "EdgeDrop", "EdgeWalkInGroups", "RandomDrop", "CenterDrop" }, out errorCode, out errorMessage);
                case AIActionNames.RequestRaidWaves:
                    return RequireInt(normalized, "waves", 2, 6, out errorCode, out errorMessage);
                case AIActionNames.RequestItemAirdrop:
                    return RequireItemRows(normalized, "need_items", out errorCode, out errorMessage) &&
                           RequireItemRows(normalized, "payment_items", out errorCode, out errorMessage) &&
                           OptionalEnum(normalized, "scenario", new[] { "general", "trade", "ransom" }, out errorCode, out errorMessage);
                case AIActionNames.AcceptItemAirdrop:
                    return RequireString(normalized, "request_id", out errorCode, out errorMessage);
                case AIActionNames.RequestInfo:
                    return RequireEnum(normalized, "info_type", new[] { "prisoner" }, out errorCode, out errorMessage);
                case AIActionNames.PayPrisonerRansom:
                    return RequireInt(normalized, "target_pawn_load_id", 1, int.MaxValue, out errorCode, out errorMessage) &&
                           RequireInt(normalized, "offer_silver", 1, int.MaxValue, out errorCode, out errorMessage) &&
                           OptionalEnum(normalized, "payment_mode", new[] { "silver" }, out errorCode, out errorMessage);
                case AIActionNames.TriggerIncident:
                    return RequireString(normalized, "defName", out errorCode, out errorMessage) && OptionalInt(normalized, "amount", int.MinValue, int.MaxValue, out errorCode, out errorMessage);
                case AIActionNames.CreateQuest:
                    return RequireString(normalized, "questDefName", out errorCode, out errorMessage) && OptionalString(normalized, "askerFaction", out errorCode, out errorMessage) && OptionalInt(normalized, "points", 0, int.MaxValue, out errorCode, out errorMessage);
                case AIActionNames.SendImage:
                    return RequireString(normalized, "template_id", out errorCode, out errorMessage) && OptionalString(normalized, "extra_prompt", out errorCode, out errorMessage) && OptionalString(normalized, "caption", out errorCode, out errorMessage) && OptionalString(normalized, "size", out errorCode, out errorMessage) && OptionalBool(normalized, "watermark", out errorCode, out errorMessage);
                case AIActionNames.PublishPublicPost:
                    return RequireEnum(normalized, "category", new[] { "Military", "Economic", "Diplomatic", "Anomaly" }, out errorCode, out errorMessage) && RequireInt(normalized, "sentiment", -2, 2, out errorCode, out errorMessage) && OptionalString(normalized, "summary", out errorCode, out errorMessage) && OptionalString(normalized, "targetFaction", out errorCode, out errorMessage) && OptionalString(normalized, "intentHint", out errorCode, out errorMessage);
                default:
                    return true;
            }
        }

        private static string BuildDescription(string name, ApiActionConfig action)
        {
            if (name == AIActionNames.RequestItemAirdrop)
            {
                return "Propose a new item-airdrop trade with one or more requested items and one or more payment items. " +
                    "Use only when there is no active trade card. Compare the market totals supplied in context and decide the price as the faction; the runtime does not apply a hidden price veto. " +
                    "This proposes terms and does not complete the trade before player confirmation.";
            }
            if (name == AIActionNames.AcceptItemAirdrop)
            {
                return "Accept the complete immutable item-airdrop quote identified by request_id. Copy request_id exactly from the current trade-card reference. " +
                    "A previous refusal does not prevent later acceptance. A counteroffer is not acceptance. Do not add price, item, or count parameters, and do not claim completion before player confirmation.";
            }

            string description = action?.Description?.Trim() ?? string.Empty;
            string requirement = action?.Requirement?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(requirement) ? description : description + " Requirements: " + requirement;
        }

        private static string Obj(string properties, string required = null)
        {
            string requiredPart = string.IsNullOrWhiteSpace(required) ? string.Empty : ",\"required\":[" + required + "]";
            return "{\"type\":\"object\",\"properties\":{" + properties + "}" + requiredPart + ",\"additionalProperties\":false}";
        }

        private static HashSet<string> AllowedKeys(string name)
        {
            string[] keys;
            switch (name)
            {
                case AIActionNames.AdjustGoodwill: keys = new[] { "amount", "reason" }; break;
                case AIActionNames.SendGift: keys = new[] { "silver", "goodwill_gain" }; break;
                case AIActionNames.RequestAid: keys = new[] { "type" }; break;
                case AIActionNames.DeclareWar:
                case AIActionNames.RejectRequest:
                case AIActionNames.ExitDialogue:
                case AIActionNames.GoOffline:
                case AIActionNames.SetDnd: keys = new[] { "reason" }; break;
                case AIActionNames.MakePeace: keys = new[] { "cost" }; break;
                case AIActionNames.RequestCaravan: keys = new[] { "type" }; break;
                case AIActionNames.RequestVisitor:
                case AIActionNames.RequestRaidCallEveryone: keys = Array.Empty<string>(); break;
                case AIActionNames.RequestRaid: keys = new[] { "strategy", "arrival" }; break;
                case AIActionNames.RequestRaidWaves: keys = new[] { "waves" }; break;
                case AIActionNames.RequestItemAirdrop: keys = new[] { "need_items", "payment_items", "scenario" }; break;
                case AIActionNames.AcceptItemAirdrop: keys = new[] { "request_id" }; break;
                case AIActionNames.RequestInfo: keys = new[] { "info_type" }; break;
                case AIActionNames.PayPrisonerRansom: keys = new[] { "target_pawn_load_id", "offer_silver", "payment_mode" }; break;
                case AIActionNames.TriggerIncident: keys = new[] { "defName", "amount" }; break;
                case AIActionNames.CreateQuest: keys = new[] { "questDefName", "askerFaction", "points" }; break;
                case AIActionNames.SendImage: keys = new[] { "template_id", "extra_prompt", "caption", "size", "watermark" }; break;
                case AIActionNames.PublishPublicPost: keys = new[] { "category", "sentiment", "summary", "targetFaction", "intentHint" }; break;
                default: return null;
            }
            return new HashSet<string>(keys, StringComparer.Ordinal);
        }

        private static bool RequireItemRows(Dictionary<string, object> values, string key, out string code, out string message)
        {
            if (!values.TryGetValue(key, out object raw) || !(raw is List<object> rows) || rows.Count == 0 || rows.Count > MaxAirdropItemRows)
            {
                return Fail("invalid_arguments", $"Parameter '{key}' must contain between 1 and {MaxAirdropItemRows} rows.", out code, out message);
            }
            foreach (object rawRow in rows)
            {
                if (!(rawRow is Dictionary<string, object> row) || row.Keys.Any(k => k != "item" && k != "count"))
                {
                    return Fail("invalid_arguments", $"Every '{key}' row must contain only item and count.", out code, out message);
                }
                if (!RequireString(row, "item", out code, out message) || !RequireInt(row, "count", 1, MaxAirdropItemCount, out code, out message))
                {
                    message = key + ": " + message;
                    return false;
                }
            }
            code = string.Empty;
            message = string.Empty;
            return true;
        }

        private static bool RequireString(Dictionary<string, object> values, string key, out string code, out string message)
        {
            if (!values.TryGetValue(key, out object raw) || !(raw is string text) || string.IsNullOrWhiteSpace(text))
            {
                return Fail("invalid_arguments", $"Parameter '{key}' must be a non-empty string.", out code, out message);
            }
            values[key] = text.Trim();
            code = message = string.Empty;
            return true;
        }

        private static bool OptionalString(Dictionary<string, object> values, string key, out string code, out string message)
        {
            if (!values.ContainsKey(key)) { code = message = string.Empty; return true; }
            if (values[key] == null) { values.Remove(key); code = message = string.Empty; return true; }
            if (!(values[key] is string text)) return Fail("invalid_arguments", $"Parameter '{key}' must be a string.", out code, out message);
            values[key] = text;
            code = message = string.Empty;
            return true;
        }

        private static bool RequireEnum(Dictionary<string, object> values, string key, string[] allowed, out string code, out string message)
        {
            if (!RequireString(values, key, out code, out message)) return false;
            string value = (string)values[key];
            string canonical = allowed.FirstOrDefault(item => string.Equals(item, value, StringComparison.Ordinal));
            if (canonical == null) return Fail("invalid_arguments", $"Parameter '{key}' must be one of: {string.Join(", ", allowed)}.", out code, out message);
            values[key] = canonical;
            return true;
        }

        private static bool OptionalEnum(Dictionary<string, object> values, string key, string[] allowed, out string code, out string message)
        {
            if (!values.ContainsKey(key)) { code = message = string.Empty; return true; }
            if (values[key] == null) { values.Remove(key); code = message = string.Empty; return true; }
            return RequireEnum(values, key, allowed, out code, out message);
        }

        private static bool RequireInt(Dictionary<string, object> values, string key, int min, int max, out string code, out string message)
        {
            if (!values.TryGetValue(key, out object raw) || !TryExactInt(raw, out int value) || value < min || value > max)
            {
                return Fail("invalid_arguments", $"Parameter '{key}' must be an integer between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.", out code, out message);
            }
            values[key] = value;
            code = message = string.Empty;
            return true;
        }

        private static bool OptionalInt(Dictionary<string, object> values, string key, int min, int max, out string code, out string message)
        {
            if (!values.ContainsKey(key)) { code = message = string.Empty; return true; }
            if (values[key] == null) { values.Remove(key); code = message = string.Empty; return true; }
            return RequireInt(values, key, min, max, out code, out message);
        }

        private static bool OptionalBool(Dictionary<string, object> values, string key, out string code, out string message)
        {
            if (!values.ContainsKey(key)) { code = message = string.Empty; return true; }
            if (values[key] == null) { values.Remove(key); code = message = string.Empty; return true; }
            if (!(values[key] is bool)) return Fail("invalid_arguments", $"Parameter '{key}' must be a boolean.", out code, out message);
            code = message = string.Empty;
            return true;
        }

        private static bool TryExactInt(object raw, out int value)
        {
            value = 0;
            try
            {
                if (raw is int intValue) { value = intValue; return true; }
                if (raw is long longValue && longValue >= int.MinValue && longValue <= int.MaxValue) { value = (int)longValue; return true; }
                if (raw is decimal decimalValue && decimal.Truncate(decimalValue) == decimalValue && decimalValue >= int.MinValue && decimalValue <= int.MaxValue) { value = (int)decimalValue; return true; }
                if (raw is double doubleValue && Math.Abs(doubleValue % 1d) < double.Epsilon && doubleValue >= int.MinValue && doubleValue <= int.MaxValue) { value = (int)doubleValue; return true; }
            }
            catch { }
            return false;
        }

        private static bool Fail(string codeValue, string messageValue, out string code, out string message)
        {
            code = codeValue;
            message = messageValue;
            return false;
        }
    }
}
