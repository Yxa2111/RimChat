using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimChat.Dialogue
{
    public sealed class DialogueOpenIntent
    {
        public DialogueRuntimeContext RuntimeContext { get; }
        public bool MuteOpenSound { get; }
        public string ProactiveOpening { get; }
        public string ProactiveChoiceIntent { get; }

        private DialogueOpenIntent(DialogueRuntimeContext runtimeContext, bool muteOpenSound, string proactiveOpening, string proactiveChoiceIntent = null)
        {
            RuntimeContext = runtimeContext;
            MuteOpenSound = muteOpenSound;
            ProactiveOpening = proactiveOpening;
            ProactiveChoiceIntent = proactiveChoiceIntent;
        }

        public static DialogueOpenIntent CreateDiplomacy(Faction faction, Pawn negotiator = null, Map map = null, bool muteOpenSound = false)
        {
            DialogueRuntimeContext context = DialogueRuntimeContext.CreateDiplomacy(faction, negotiator, map);
            return new DialogueOpenIntent(context, muteOpenSound, string.Empty);
        }

        public static DialogueOpenIntent CreateRpg(Pawn initiator, Pawn target, Map map = null, string proactiveOpening = null, string proactiveChoiceIntent = null)
        {
            DialogueRuntimeContext context = DialogueRuntimeContext.CreateRpg(initiator, target, map);
            return new DialogueOpenIntent(context, false, proactiveOpening, proactiveChoiceIntent);
        }

        public static DialogueOpenIntent CreateRpgGroup(Pawn initiator, List<Pawn> participants, Map map = null, string proactiveOpening = null)
        {
            DialogueRuntimeContext context = DialogueRuntimeContext.CreateRpgGroup(initiator, participants, map);
            return new DialogueOpenIntent(context, false, proactiveOpening);
        }
    }
}
