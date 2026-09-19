using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PixelCrushers.DialogueSystem;
using UnityEngine;

namespace DynamicEpilogues;

[BepInPlugin(
    MyPluginInfo.PLUGIN_GUID,
    MyPluginInfo.PLUGIN_NAME,
    MyPluginInfo.PLUGIN_VERSION
)]
public sealed class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger { get; private set; }

    private Harmony harmony;

    private void Awake()
    {
        Logger = base.Logger;

        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        Logger.LogInfo("=== DYNAMIC EPILOGUES 1.0.1 PORT BUILD IS RUNNING ===");
        Logger.LogInfo(
            "DynamicEpilogues assembly path: " +
            Assembly.GetExecutingAssembly().Location
        );

        Logger.LogInfo("DynamicEpilogues: installing Harmony patches");

        try
        {
            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

            Logger.LogInfo("DynamicEpilogues: Harmony patches installed");
        }
        catch (Exception exception)
        {
            Logger.LogError("DynamicEpilogues: Harmony PatchAll FAILED:");
            Logger.LogError(exception);
        }
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
    }
}

internal enum EpilogueRoute
{
    Revolution,
    Reform
}

internal static class DynamicEpilogueRuntime
{
    private const int PreferredNarrationChunkLength = 170;
    private const int MinimumNarrationChunkLength = 90;

    private const string RevolutionAnchor =
        "Chapter20R/End/Victory3";

    private const string ReformAnchor =
        "Chapter20A/End/Crawford";

    private const string RevolutionTitle =
        "Chapter20R/End/DynamicEpilogues";

    private const string ReformTitle =
        "Chapter20A/End/DynamicEpilogues";

    private const string RevolutionResourceName =
        "DynamicEpilogues.Data.revolution_epilogs.txt";

    private const string ReformResourceName =
        "DynamicEpilogues.Data.reform_epilogs.txt";

    private const int NarratorActorId = 11;

    private static readonly Dictionary<string, int> ActorIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "Abigail", 36 },
            { "Ajax", 58 },
            { "Arland", 22 },
            { "Benjen", 34 },
            { "Cassidy", 83 },
            { "Cedric", 52 },
            { "Corvin", 69 },
            { "Douglas", 67 },
            { "Edward", 65 },
            { "Elias", 41 },
            { "Finn", 59 },
            { "Hilda", 39 },
            { "Illyana", 12 },
            { "Jack", 91 },
            { "Jorah", 72 },
            { "Kaelith", 53 },
            { "Marcus", 19 },
            { "Melanie", 62 },
            { "Narrator", NarratorActorId },
            { "Noah", 93 },
            { "Phoebe", 37 },
            { "Quincy", 66 },
            { "Reyson", 40 },
            { "Rho", 33 },
            { "Salvatore", 70 },
            { "Shiva", 68 },
            { "Slyker", 18 },
            { "Sven", 38 },
            { "Sylvia", 92 },
            { "Talon", 60 },
            { "Wesley", 27 }
        };

    private static readonly Dictionary<DialogueDatabase, RuntimeState> States =
        new();

    private static readonly Dictionary<int, Dictionary<int, int[]>> FocusPanelsByEntry =
        new();

    private static readonly Dictionary<int, Dictionary<int, int[]>> FocusPersistenceCheckByEntry =
        new();

    private static bool loggedFocusPersistenceCheck;

    private static readonly Dictionary<ushort, OpCode> SingleByteOpCodes =
        new();

    private static readonly Dictionary<ushort, OpCode> MultiByteOpCodes =
        new();

    private static readonly EpilogueSection[] RevolutionSections =
        EpilogueParser.Parse(
            ReadEmbeddedText(RevolutionResourceName),
            "Revolution"
        );

    private static readonly EpilogueSection[] ReformSections =
        EpilogueParser.Parse(
            ReadEmbeddedText(ReformResourceName),
            "Reform"
        );

    private static ChapterController lastAnchorController;
    private static EpilogueRoute? lastAnchorRoute;
    private static bool anchorsValidated;
    private static bool anchorsValid;
    private static bool loggedWrapperEntered;
    private static bool loggedDialogueUIDiagnostics;
    private static readonly HashSet<string> LoggedSubtitlePanelSnapshots =
        new();
    private static readonly HashSet<string> LoggedTransitionInspections =
        new();
    private static readonly List<SavedUnfocusTrigger> SavedUnfocusTriggers =
        new();
    private const string WrapperSubtitlePanelTypeName =
        "PixelCrushers.DialogueSystem.Wrappers.StandardUISubtitlePanel";
    private const string FarLeftPanelName =
        "Subtitle Panel Far Left";
    private const string CenterLeftPanelName =
        "Subtitle Panel Center Left";
    private const string FarRightPanelName =
        "Subtitle Panel Far Right";

    static DynamicEpilogueRuntime()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(
                     BindingFlags.Public |
                     BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            ushort value = unchecked((ushort)opCode.Value);

            if (value <= 0xFF)
            {
                SingleByteOpCodes[value] = opCode;
            }
            else
            {
                MultiByteOpCodes[(ushort)(value & 0xFF)] = opCode;
            }
        }

        Plugin.Logger?.LogInfo(
            $"Parsed Revolution epilogue sections: " +
            $"{RevolutionSections.Count(section => section.Kind == EpilogueKind.Epilogue)}."
        );

        Plugin.Logger?.LogInfo(
            $"Parsed Reform epilogue sections: " +
            $"{ReformSections.Count(section => section.Kind == EpilogueKind.Epilogue)}."
        );
    }

    public static bool NoteConversationStart(
        ChapterController controller,
        string conversationId)
    {
        if (IsChapter20EndingConversation(conversationId))
        {
            Plugin.Logger.LogInfo(
                $"TWR StartConversation observed: {conversationId}"
            );
        }

        if (controller is not Chapter20Controller)
        {
            return true;
        }

        EpilogueRoute? route =
            string.Equals(
                conversationId,
                RevolutionAnchor,
                StringComparison.Ordinal)
                ? EpilogueRoute.Revolution
                : string.Equals(
                    conversationId,
                    ReformAnchor,
                    StringComparison.Ordinal)
                    ? EpilogueRoute.Reform
                    : null;

        if (!route.HasValue)
        {
            return true;
        }

        lastAnchorController = controller;
        lastAnchorRoute = route;

        if (route.Value == EpilogueRoute.Reform)
        {
            Plugin.Logger.LogInfo(
                "Reform DynamicEpilogues will run immediately after " +
                $"{ReformAnchor} completes."
            );
        }

        return true;
    }

    public static IEnumerator WrapVictory(
        IEnumerator inner,
        Chapter20Controller controller)
    {
        if (!loggedWrapperEntered)
        {
            loggedWrapperEntered = true;
            Plugin.Logger.LogInfo(
                "DynamicEpilogues Victory IEnumerator wrapper entered."
            );
            Plugin.Logger.LogInfo(
                "DynamicEpilogues target method is the original " +
                "IEnumerator; generated MoveNext is inspected below."
            );
        }

        if (!ValidateAnchors())
        {
            while (inner.MoveNext())
            {
                yield return inner.Current;
            }

            yield break;
        }

        EpilogueRoute? pendingRoute = null;

        while (inner.MoveNext())
        {
            object current = inner.Current;
            EpilogueRoute? startedRoute =
                ConsumeAnchor(controller);

            if (startedRoute.HasValue)
            {
                pendingRoute = startedRoute.Value;
            }

            yield return current;

            if (pendingRoute.HasValue &&
                current is WaitForSeconds)
            {
                EpilogueRoute route =
                    pendingRoute.Value;

                pendingRoute = null;

                Plugin.Logger.LogInfo(
                    route == EpilogueRoute.Reform
                        ? ">>> REFORM DYNAMIC EPILOGUE HELPER ENTERED <<<"
                        : ">>> REVOLUTION DYNAMIC EPILOGUE HELPER ENTERED <<<"
                );

                IEnumerator epilogue =
                    PlayDynamicEpilogues(
                        controller,
                        route
                    );

                while (epilogue.MoveNext())
                {
                    yield return epilogue.Current;
                }
            }
        }
    }

    private static EpilogueRoute? ConsumeAnchor(
        Chapter20Controller controller)
    {
        if (lastAnchorController != controller ||
            !lastAnchorRoute.HasValue)
        {
            return null;
        }

        EpilogueRoute route = lastAnchorRoute.Value;

        lastAnchorController = null;
        lastAnchorRoute = null;

        return route;
    }

    private static IEnumerator PlayDynamicEpilogues(
        Chapter20Controller controller,
        EpilogueRoute route)
    {
        if (!PrepareConversation(route, out string title, out int entryCount))
        {
            yield break;
        }

        if (entryCount <= 0)
        {
            Plugin.Logger.LogInfo(
                $"{route} dynamic epilogues produced no visible entries; " +
                "skipping StartConversation."
            );
            yield break;
        }

        DialogueDatabase database =
            DialogueManager.MasterDatabase;

        Plugin.Logger.LogInfo(
            $"Looking up {title} before StartConversation: " +
            $"{(database?.GetConversation(title) != null ? "FOUND" : "NOT FOUND")}"
        );

        Plugin.Logger.LogInfo(
            $"Starting {title}"
        );

        SuppressPortraitUnfocusTriggers();

        try
        {
            controller.StartConversation(
                title,
                GameAssets.Instance.worldMap,
                false,
                false
            );

            Plugin.Logger.LogInfo(
                $"StartConversation returned. Conversing={controller.Conversing()}."
            );

            LogDialogueUIRuntimeType();

            while (controller.Conversing())
            {
                yield return null;
            }

            Plugin.Logger.LogInfo(
                route == EpilogueRoute.Reform
                    ? "Dynamic Reform conversation finished."
                    : "Dynamic Revolution conversation finished."
            );

            yield return new WaitForSeconds(1f);
        }
        finally
        {
            RestorePortraitUnfocusTriggers();
        }
    }

    private static bool PrepareConversation(
        EpilogueRoute route,
        out string title,
        out int visibleEntryCount)
    {
        Plugin.Logger.LogInfo("Preparing dynamic epilogues");

        title = GetTitle(route);
        visibleEntryCount = 0;

        try
        {
            DialogueDatabase database =
                DialogueManager.MasterDatabase;

            Plugin.Logger.LogInfo(
                "DialogueManager.masterDatabase null: " +
                (database == null)
            );

            if (database == null)
            {
                Plugin.Logger.LogError(
                    $"Cannot prepare {title}: master dialogue database is null."
                );
                return false;
            }

            int highestExisting =
                database.conversations
                    .Select(conversation => conversation.id)
                    .DefaultIfEmpty(0)
                    .Max();

            Plugin.Logger.LogInfo(
                $"Master database conversation count: {database.conversations.Count}"
            );

            Plugin.Logger.LogInfo(
                $"Highest existing conversation ID: {highestExisting}"
            );

            Plugin.Logger.LogInfo(
                $"Reform eligible epilogues: {CountEligibleEpilogues(ReformSections)}"
            );

            Plugin.Logger.LogInfo(
                $"Revolution eligible epilogues: {CountEligibleEpilogues(RevolutionSections)}"
            );

            RuntimeState state =
                GetOrCreateState(database);

            if (!state.CanUseConversations)
            {
                return false;
            }

            Conversation conversation =
                route == EpilogueRoute.Revolution
                    ? state.RevolutionConversation
                    : state.ReformConversation;

            if (conversation == null)
            {
                Plugin.Logger.LogError(
                    $"Cannot prepare {title}: conversation slot was not created."
                );
                return false;
            }

            BuildConversation(
                conversation,
                route == EpilogueRoute.Revolution
                    ? RevolutionSections
                    : ReformSections,
                route,
                out int eligibleSections,
                out visibleEntryCount
            );

            Plugin.Logger.LogInfo(
                $"{route} dynamic epilogues: {eligibleSections} eligible " +
                $"sections, {visibleEntryCount} entries."
            );

            Plugin.Logger.LogInfo(
                $"Added {title} | ID={conversation.id} | " +
                $"Entries={conversation.dialogueEntries.Count}"
            );

            Plugin.Logger.LogInfo(
                $"Lookup {title} after registration: " +
                $"{(database.GetConversation(title) != null ? "FOUND" : "NOT FOUND")}"
            );

            DumpConversation(conversation);

            return true;
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError(
                $"Failed to prepare {title}: {exception}"
            );
            return false;
        }
    }

    private static RuntimeState GetOrCreateState(
        DialogueDatabase database)
    {
        if (States.TryGetValue(database, out RuntimeState state))
        {
            if (state.IsStillRegisteredIn(database))
            {
                return state;
            }

            States.Remove(database);
        }

        state = CreateState(database);
        States[database] = state;
        return state;
    }

    private static RuntimeState CreateState(
        DialogueDatabase database)
    {
        Conversation existingRevolution =
            database.GetConversation(RevolutionTitle);

        Conversation existingReform =
            database.GetConversation(ReformTitle);

        if (existingRevolution != null ||
            existingReform != null)
        {
            Plugin.Logger.LogError(
                "Dynamic epilogue conversation title conflict. " +
                $"Existing Revolution={existingRevolution != null}, " +
                $"existing Reform={existingReform != null}. " +
                "Leaving existing conversations untouched."
            );

            return RuntimeState.Conflict;
        }

        int highestConversationId =
            database.conversations
                .Select(conversation => conversation.id)
                .DefaultIfEmpty(0)
                .Max();

        int revolutionId = highestConversationId + 100;
        int reformId = revolutionId + 1;

        Plugin.Logger.LogInfo(
            $"Highest existing conversation ID: {highestConversationId}"
        );

        Plugin.Logger.LogInfo(
            "Creating conversations via Template.FromDefault().CreateConversation"
        );

        Conversation revolution =
            CreateConversationShell(revolutionId, RevolutionTitle);

        Conversation reform =
            CreateConversationShell(reformId, ReformTitle);

        database.conversations.Add(revolution);
        database.conversations.Add(reform);

        Plugin.Logger.LogInfo(
            $"Added {RevolutionTitle} | ID={revolution.id} | " +
            $"Entries={revolution.dialogueEntries.Count}"
        );

        Plugin.Logger.LogInfo(
            $"Added {ReformTitle} | ID={reform.id} | " +
            $"Entries={reform.dialogueEntries.Count}"
        );

        Plugin.Logger.LogInfo(
            $"Lookup {ReformTitle} after registration: " +
            $"{(database.GetConversation(ReformTitle) != null ? "FOUND" : "NOT FOUND")}"
        );

        Plugin.Logger.LogInfo(
            $"Lookup {RevolutionTitle} after registration: " +
            $"{(database.GetConversation(RevolutionTitle) != null ? "FOUND" : "NOT FOUND")}"
        );

        return new RuntimeState(
            revolution,
            reform
        );
    }

    private static Conversation CreateConversationShell(
        int id,
        string title)
    {
        Template template =
            Template.FromDefault();

        Conversation conversation =
            template.CreateConversation(id, title);

        Plugin.Logger.LogInfo(
            "Created conversation shell:" +
            $" ID={conversation.id}" +
            $" Title={conversation.Title}" +
            $" FieldsNull={conversation.fields == null}" +
            $" DialogueEntriesNull={conversation.dialogueEntries == null}"
        );

        BuildRootOnly(conversation);

        return conversation;
    }

    private static void BuildRootOnly(
        Conversation conversation)
    {
        Template template =
            Template.FromDefault();

        DialogueEntry root =
            template.CreateDialogueEntry(
                0,
                conversation.id,
                "START"
            );

        root.isRoot = true;
        root.ActorID = NarratorActorId;
        root.ConversantID = NarratorActorId;
        root.DialogueText = string.Empty;
        root.outgoingLinks.Clear();

        Plugin.Logger.LogInfo(
            "Created START entry:" +
            $" ID={root.id}" +
            $" FieldsNull={root.fields == null}" +
            $" OutgoingLinksNull={root.outgoingLinks == null}"
        );

        conversation.dialogueEntries.Clear();
        conversation.dialogueEntries.Add(root);
    }

    private static void BuildConversation(
        Conversation conversation,
        EpilogueSection[] sections,
        EpilogueRoute route,
        out int eligibleSections,
        out int visibleEntryCount)
    {
        Template template =
            Template.FromDefault();

        DialogueEntry root =
            template.CreateDialogueEntry(
                0,
                conversation.id,
                "START"
            );

        root.isRoot = true;
        root.ActorID = NarratorActorId;
        root.ConversantID = NarratorActorId;
        root.DialogueText = string.Empty;
        root.outgoingLinks.Clear();

        var entries =
            new List<DialogueEntry> { root };

        FocusPanelsByEntry[conversation.id] =
            new Dictionary<int, int[]>();

        FocusPersistenceCheckByEntry[conversation.id] =
            new Dictionary<int, int[]>();

        loggedFocusPersistenceCheck = false;

        DialogueEntry previous = root;
        int nextEntryId = 1;
        eligibleSections = 0;
        int previousEpilogueParticipantCount = 0;

        foreach (EpilogueSection section in sections)
        {
            if (!ShouldInclude(section))
            {
                continue;
            }

            eligibleSections++;

            if (section.Kind == EpilogueKind.Epilogue)
            {
                int participantCount =
                    section.Characters.Length;

                Plugin.Logger.LogInfo(
                    "Building epilogue section: " +
                    $"participants={string.Join(" & ", section.Characters)} " +
                    $"visibleTitle={section.Title}"
                );

                if (previousEpilogueParticipantCount == 2 &&
                    participantCount == 1)
                {
                    Plugin.Logger.LogInfo(
                        "Inserted portrait panel cleanup: " +
                        "HidePanel(0); HidePanel(3)."
                    );

                    foreach (EntrySpec clearSpec in BuildPortraitClearSpecs())
                    {
                        DialogueEntry clearEntry =
                            template.CreateDialogueEntry(
                                nextEntryId,
                                conversation.id,
                                clearSpec.ActorName
                            );

                        clearEntry.ActorID = clearSpec.ActorId;
                        clearEntry.ConversantID = clearSpec.ConversantId;
                        clearEntry.DialogueText = clearSpec.Text;
                        clearEntry.Sequence = clearSpec.Sequence;
                        clearEntry.outgoingLinks.Clear();

                        previous.outgoingLinks.Add(
                            new Link(
                                conversation.id,
                                previous.id,
                                conversation.id,
                                clearEntry.id
                            )
                        );

                        entries.Add(clearEntry);
                        previous = clearEntry;
                        nextEntryId++;
                    }
                }
            }

            bool openFirstPortraitPanel =
                section.Kind == EpilogueKind.Epilogue &&
                section.Characters.Length == 1 &&
                previousEpilogueParticipantCount == 2;

            bool openSecondPortraitPanel =
                section.Kind == EpilogueKind.Epilogue &&
                section.Characters.Length == 2 &&
                previousEpilogueParticipantCount == 1;

            foreach (EntrySpec spec in BuildEntrySpecs(
                section,
                openFirstPortraitPanel,
                openSecondPortraitPanel))
            {
                DialogueEntry entry =
                    template.CreateDialogueEntry(
                        nextEntryId,
                        conversation.id,
                        spec.ActorName
                    );

                entry.ActorID = spec.ActorId;
                entry.ConversantID = spec.ConversantId;
                entry.DialogueText = spec.Text;
                entry.Sequence = spec.Sequence;
                entry.outgoingLinks.Clear();

                if (spec.FocusOnce &&
                    spec.FocusPanels.Length > 0)
                {
                    RegisterFocusPanels(
                        conversation.id,
                        entry.id,
                        spec.FocusPanels
                    );
                }

                if (spec.CheckFocusPersistence &&
                    spec.FocusPanels.Length > 0)
                {
                    RegisterFocusPersistenceCheck(
                        conversation.id,
                        entry.id,
                        spec.FocusPanels
                    );
                }

                previous.outgoingLinks.Add(
                    new Link(
                        conversation.id,
                        previous.id,
                        conversation.id,
                        entry.id
                    )
                );

                entries.Add(entry);
                previous = entry;
                nextEntryId++;
            }

            if (section.Kind == EpilogueKind.Epilogue)
            {
                previousEpilogueParticipantCount =
                    section.Characters.Length;
            }
        }

        conversation.dialogueEntries.Clear();
        conversation.dialogueEntries.AddRange(entries);

        visibleEntryCount = entries.Count - 1;
    }

    private static void DumpConversation(
        Conversation conversation)
    {
        Plugin.Logger.LogInfo(
            $"----- DynamicEpilogues conversation dump: " +
            $"{conversation.Title} | ID={conversation.id} | " +
            $"Entries={conversation.dialogueEntries.Count} -----"
        );

        foreach (DialogueEntry entry in conversation.dialogueEntries)
        {
            string links =
                entry.outgoingLinks == null ||
                entry.outgoingLinks.Count == 0
                    ? "<none>"
                    : string.Join(
                        ", ",
                        entry.outgoingLinks.Select(link =>
                            $"{link.originConversationID}:{link.originDialogueID}" +
                            " -> " +
                            $"{link.destinationConversationID}:{link.destinationDialogueID}"
                        )
                    );

            Plugin.Logger.LogInfo(
                $"Entry {entry.id} | Root={entry.isRoot} | " +
                $"ActorID={entry.ActorID} | ConversantID={entry.ConversantID} | " +
                $"Text=\"{NormalizeForLog(entry.DialogueText)}\" | " +
                $"TextLength={(entry.DialogueText ?? string.Empty).Length} | " +
                $"Sequence=\"{NormalizeForLog(entry.Sequence)}\" | " +
                $"OutgoingLinks={links}"
            );
        }

        Plugin.Logger.LogInfo(
            "----- End DynamicEpilogues conversation dump -----"
        );
    }

    private static string NormalizeForLog(
        string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static bool ShouldInclude(
        EpilogueSection section)
    {
        if (section.Kind != EpilogueKind.Epilogue)
        {
            return true;
        }

        foreach (string character in section.Characters)
        {
            bool alive =
                IsCharacterAlive(character, out int? vitality);

            Plugin.Logger.LogInfo(
                $"Dynamic epilogue: {character} alive={alive}" +
                (vitality.HasValue
                    ? $" CurrentVitality={vitality.Value}"
                    : " CurrentVitality key absent")
            );

            if (!alive)
            {
                Plugin.Logger.LogInfo(
                    $"Skipping {section.Characters.Length}-person epilogue " +
                    $"{string.Join(" & ", section.Characters)} because " +
                    $"{character} is dead."
                );
                return false;
            }
        }

        Plugin.Logger.LogInfo(
            $"Including: {string.Join(" & ", section.Characters)} - " +
            $"{section.Title}"
        );

        return true;
    }

    private static IEnumerable<EntrySpec> BuildEntrySpecs(
        EpilogueSection section,
        bool openFirstPortraitPanel,
        bool openSecondPortraitPanel)
    {
        if (section.Kind == EpilogueKind.End)
        {
            yield break;
        }

        if (section.Kind == EpilogueKind.Introduction)
        {
            bool firstBodyChunk = true;

            foreach (string chunk in SplitNarrationText(section.Paragraphs))
            {
                yield return new EntrySpec(
                    "Narrator",
                    NarratorActorId,
                    NarratorActorId,
                    "[panel=1]" + chunk,
                    "",
                    false,
                    firstBodyChunk,
                    0
                );

                firstBodyChunk = false;
            }

            yield break;
        }

        string displayTitle =
            section.Title;

        if (section.Characters.Length == 1)
        {
            string characterName =
                section.Characters[0];

            int actorId =
                GetActorId(characterName);

            yield return new EntrySpec(
                characterName,
                actorId,
                NarratorActorId,
                BuildPortraitSetupText(0),
                BuildSetPanelSequence(
                    actorId,
                    0,
                    openFirstPortraitPanel)
            );

            yield return new EntrySpec(
                "Narrator",
                NarratorActorId,
                NarratorActorId,
                "[panel=1]" + displayTitle,
                "",
                true,
                false,
                0
            );

            foreach (string chunk in SplitNarrationText(section.Paragraphs))
            {
                yield return new EntrySpec(
                    "Narrator",
                    NarratorActorId,
                    NarratorActorId,
                    "[panel=1]" + chunk
                );
            }

            yield break;
        }

        int firstActorId =
            GetActorId(section.Characters[0]);

        int secondActorId =
            GetActorId(section.Characters[1]);

        Plugin.Logger.LogInfo(
            "Portrait setup: " +
            $"first={section.Characters[0]} id={firstActorId} panel=0; " +
            $"second={section.Characters[1]} id={secondActorId} panel=3"
        );

        yield return new EntrySpec(
            section.Characters[0],
            firstActorId,
            secondActorId,
            BuildPortraitSetupText(0),
            BuildSetPanelSequence(firstActorId, 0, false)
        );

        yield return new EntrySpec(
            section.Characters[1],
            secondActorId,
            firstActorId,
            BuildPortraitSetupText(3),
            BuildSetPanelSequence(
                secondActorId,
                3,
                openSecondPortraitPanel)
        );

        yield return new EntrySpec(
            "Narrator",
            NarratorActorId,
            NarratorActorId,
            "[panel=1]" + displayTitle,
            "",
            true,
            false,
            0,
            3
        );

        bool firstTwoPersonBodyChunk = true;

        foreach (string chunk in SplitNarrationText(section.Paragraphs))
        {
            yield return new EntrySpec(
                "Narrator",
                NarratorActorId,
                NarratorActorId,
                "[panel=1]" + chunk,
                "",
                false,
                firstTwoPersonBodyChunk,
                0,
                3
            );

            firstTwoPersonBodyChunk = false;
        }
    }

    private static IEnumerable<EntrySpec> BuildPortraitClearSpecs()
    {
        yield return new EntrySpec(
            "Narrator",
            NarratorActorId,
            NarratorActorId,
            string.Empty,
            "required HidePanel(0); HidePanel(3); Continue()"
        );
    }

    private static IEnumerable<string> SplitNarrationText(
        IEnumerable<string> paragraphs)
    {
        foreach (string paragraph in paragraphs)
        {
            foreach (string chunk in SplitNarrationText(paragraph))
            {
                yield return chunk;
            }
        }
    }

    private static IEnumerable<string> SplitNarrationText(
        string text)
    {
        string remaining =
            (text ?? string.Empty).Trim();

        while (remaining.Length > PreferredNarrationChunkLength)
        {
            int splitIndex =
                FindNarrationSplitIndex(remaining);

            if (splitIndex <= 0 ||
                splitIndex >= remaining.Length)
            {
                break;
            }

            string chunk =
                remaining.Substring(0, splitIndex).Trim();

            if (chunk.Length > 0)
            {
                yield return chunk;
            }

            remaining =
                remaining.Substring(splitIndex).TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static int FindNarrationSplitIndex(
        string text)
    {
        string[] sentenceBoundaries =
        {
            ". ",
            "! ",
            "? "
        };

        int split =
            FindLastBoundary(
                text,
                sentenceBoundaries,
                PreferredNarrationChunkLength,
                MinimumNarrationChunkLength
            );

        if (split > 0)
        {
            return split;
        }

        string[] clauseBoundaries =
        {
            "; ",
            ": ",
            "\u2014",
            " -- ",
            " - "
        };

        split =
            FindLastBoundary(
                text,
                clauseBoundaries,
                PreferredNarrationChunkLength,
                MinimumNarrationChunkLength
            );

        if (split > 0)
        {
            return split;
        }

        split =
            FindLastBoundary(
                text,
                new[] { ", " },
                PreferredNarrationChunkLength,
                MinimumNarrationChunkLength
            );

        if (split > 0)
        {
            return split;
        }

        int spaceIndex =
            text.LastIndexOf(
                ' ',
                Math.Min(
                    PreferredNarrationChunkLength,
                    text.Length - 1
                )
            );

        return spaceIndex > 0
            ? spaceIndex
            : PreferredNarrationChunkLength;
    }

    private static int FindLastBoundary(
        string text,
        string[] boundaries,
        int maxIndex,
        int minIndex)
    {
        int bestIndex = -1;
        int cappedMaxIndex =
            Math.Min(maxIndex, text.Length - 1);

        foreach (string boundary in boundaries)
        {
            int searchIndex =
                cappedMaxIndex;

            while (searchIndex >= 0)
            {
                int index =
                    text.LastIndexOf(
                        boundary,
                        searchIndex,
                        StringComparison.Ordinal
                    );

                if (index < 0)
                {
                    break;
                }

                int splitIndex =
                    index + boundary.TrimEnd().Length;

                if (splitIndex >= minIndex)
                {
                    bestIndex =
                        Math.Max(bestIndex, splitIndex);
                    break;
                }

                searchIndex =
                    index - 1;
            }
        }

        return bestIndex;
    }

    private static string BuildPortraitSetupText(
        int panel)
    {
        return $"[panel={panel}] ";
    }

    private static string BuildSetPanelSequence(
        int actorId,
        int panel,
        bool openPanel)
    {
        string sequence =
            $"required SetPanel({actorId}, {panel}, immediate); ";

        if (openPanel)
        {
            sequence +=
                $"OpenPanel({panel}); ";
        }

        return sequence + "Continue()";
    }

    private static void LogDialogueUIRuntimeType()
    {
        IDialogueUI dialogueUI =
            DialogueManager.dialogueUI;

        Plugin.Logger.LogInfo(
            "DialogueManager.dialogueUI runtime type: " +
            (dialogueUI == null
                ? "NULL"
                : dialogueUI.GetType().FullName)
        );

        DumpDialogueUIReflectionDiagnostics(
            dialogueUI
        );
    }

    private static void DumpDialogueUIReflectionDiagnostics(
        IDialogueUI dialogueUI)
    {
        if (loggedDialogueUIDiagnostics)
        {
            return;
        }

        loggedDialogueUIDiagnostics = true;

        try
        {
            DumpTypeMembers(
                FindLoadedType(
                    "PixelCrushers.DialogueSystem.Wrappers.StandardDialogueUI"
                ),
                "PixelCrushers.DialogueSystem.Wrappers.StandardDialogueUI"
            );

            GameObject dialogueUIGameObject =
                GetDialogueUIGameObject(
                    dialogueUI
                );

            Plugin.Logger.LogInfo(
                "Dialogue UI GameObject for child-component dump: " +
                (dialogueUIGameObject == null
                    ? "NULL"
                    : dialogueUIGameObject.name)
            );

            if (dialogueUIGameObject == null)
            {
                return;
            }

            Component[] components =
                dialogueUIGameObject.GetComponentsInChildren<Component>(
                    true
                );

            Plugin.Logger.LogInfo(
                $"Dialogue UI child component count: {components.Length}"
            );

            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                Type type =
                    component.GetType();

                if (!IsSubtitlePanelRelated(type))
                {
                    continue;
                }

                Plugin.Logger.LogInfo(
                    "Dialogue UI subtitle/panel component: " +
                    $"GameObject='{component.gameObject.name}' " +
                    $"Type={type.FullName}"
                );

                DumpInterestingTypeMembers(
                    type
                );
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError(
                $"Failed to dump Dialogue UI diagnostics: {exception}"
            );
        }
    }

    private static GameObject GetDialogueUIGameObject(
        IDialogueUI dialogueUI)
    {
        if (dialogueUI is Component component)
        {
            return component.gameObject;
        }

        return DialogueManager.DisplaySettings?.dialogueUI;
    }

    private static Type FindLoadedType(
        string fullName)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, false))
            .FirstOrDefault(type => type != null);
    }

    private static void DumpTypeMembers(
        Type type,
        string label)
    {
        if (type == null)
        {
            Plugin.Logger.LogInfo(
                $"{label}: TYPE NOT FOUND"
            );
            return;
        }

        Plugin.Logger.LogInfo(
            $"{label}: Type={type.FullName}"
        );

        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            Plugin.Logger.LogInfo(
                $"{label} field: {field.FieldType.FullName} {field.Name}"
            );
        }

        foreach (PropertyInfo property in type.GetProperties(
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            Plugin.Logger.LogInfo(
                $"{label} property: {property.PropertyType.FullName} {property.Name}"
            );
        }

        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            Plugin.Logger.LogInfo(
                $"{label} method: {method.ReturnType.FullName} " +
                $"{method.Name}({FormatParameters(method)})"
            );
        }
    }

    private static void DumpInterestingTypeMembers(
        Type type)
    {
        foreach (MemberInfo member in type.GetMembers(
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            if (!IsSubtitlePanelFocusMember(member.Name))
            {
                continue;
            }

            Plugin.Logger.LogInfo(
                $"  related member: {member.MemberType} {member.Name}"
            );
        }
    }

    private static bool IsSubtitlePanelRelated(
        Type type)
    {
        string fullName =
            type.FullName ?? string.Empty;

        if (fullName.IndexOf(
                "StandardUISubtitlePanel",
                StringComparison.OrdinalIgnoreCase) >= 0 ||
            fullName.IndexOf(
                "SubtitlePanel",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return type.GetMembers(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Any(member => IsSubtitlePanelFocusMember(member.Name));
    }

    private static bool IsSubtitlePanelFocusMember(
        string name)
    {
        return name.IndexOf(
                   "StandardUISubtitlePanel",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf(
                   "SubtitlePanel",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf(
                   "Focus",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf(
                   "Unfocus",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf(
                   "GetPanel",
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FormatParameters(
        MethodInfo method)
    {
        return string.Join(
            ", ",
            method.GetParameters()
                .Select(parameter =>
                    $"{parameter.ParameterType.FullName} {parameter.Name}"
                )
        );
    }

    private static void RegisterFocusPanels(
        int conversationId,
        int entryId,
        int[] panels)
    {
        if (!FocusPanelsByEntry.TryGetValue(
                conversationId,
                out Dictionary<int, int[]> entries))
        {
            entries =
                new Dictionary<int, int[]>();

            FocusPanelsByEntry[conversationId] =
                entries;
        }

        entries[entryId] =
            panels;
    }

    private static void RegisterFocusPersistenceCheck(
        int conversationId,
        int entryId,
        int[] panels)
    {
        if (!FocusPersistenceCheckByEntry.TryGetValue(
                conversationId,
                out Dictionary<int, int[]> entries))
        {
            entries =
                new Dictionary<int, int[]>();

            FocusPersistenceCheckByEntry[conversationId] =
                entries;
        }

        entries[entryId] =
            panels;
    }

    private static void SuppressPortraitUnfocusTriggers()
    {
        if (SavedUnfocusTriggers.Count > 0)
        {
            Plugin.Logger.LogInfo(
                "Dynamic epilogue portrait Unfocus suppression is already active."
            );
            return;
        }

        GameObject dialogueUIGameObject =
            GetDialogueUIGameObject(
                DialogueManager.dialogueUI
            );

        if (dialogueUIGameObject == null)
        {
            Plugin.Logger.LogWarning(
                "Dynamic epilogue portrait Unfocus suppression skipped: " +
                "DialogueManager.dialogueUI GameObject is unavailable."
            );
            return;
        }

        Component[] subtitlePanels =
            dialogueUIGameObject
                .GetComponentsInChildren<Component>(true)
                .Where(component =>
                    component != null &&
                    string.Equals(
                        component.GetType().FullName,
                        WrapperSubtitlePanelTypeName,
                        StringComparison.Ordinal))
                .ToArray();

        Plugin.Logger.LogInfo(
            "Dynamic epilogue portrait Unfocus suppression enabled."
        );

        SuppressPanelUnfocusTrigger(
            subtitlePanels,
            FarLeftPanelName,
            "Far Left"
        );

        SuppressPanelUnfocusTrigger(
            subtitlePanels,
            FarRightPanelName,
            "Far Right"
        );
    }

    private static void SuppressPanelUnfocusTrigger(
        Component[] subtitlePanels,
        string panelName,
        string logName)
    {
        Component panel =
            FindPanelComponent(
                subtitlePanels,
                panelName
            );

        if (panel == null)
        {
            Plugin.Logger.LogWarning(
                $"{logName} portrait panel not found for Unfocus suppression."
            );
            return;
        }

        FieldInfo field =
            FindInstanceField(
                panel.GetType(),
                "unfocusAnimationTrigger"
            );

        if (field == null ||
            field.FieldType != typeof(string))
        {
            Plugin.Logger.LogWarning(
                $"{logName} unfocusAnimationTrigger field not found on " +
                $"{panel.GetType().FullName}."
            );
            return;
        }

        string original =
            (string)field.GetValue(panel);

        SavedUnfocusTriggers.Add(
            new SavedUnfocusTrigger(
                panel,
                field,
                original,
                logName
            )
        );

        Plugin.Logger.LogInfo(
            $"{logName} original unfocusAnimationTrigger='{original}'"
        );

        field.SetValue(
            panel,
            string.Empty
        );

        Plugin.Logger.LogInfo(
            $"{logName} unfocusAnimationTrigger='{field.GetValue(panel)}'"
        );
    }

    private static void RestorePortraitUnfocusTriggers()
    {
        if (SavedUnfocusTriggers.Count == 0)
        {
            return;
        }

        Plugin.Logger.LogInfo(
            "Restoring dynamic epilogue portrait panel animation triggers."
        );

        foreach (SavedUnfocusTrigger saved in SavedUnfocusTriggers.ToArray())
        {
            try
            {
                if (saved.Panel == null ||
                    saved.Field == null)
                {
                    Plugin.Logger.LogWarning(
                        $"{saved.LogName} unfocusAnimationTrigger restore skipped: " +
                        "panel or field is unavailable."
                    );
                    continue;
                }

                saved.Field.SetValue(
                    saved.Panel,
                    saved.OriginalValue
                );

                Plugin.Logger.LogInfo(
                    $"{saved.LogName} unfocusAnimationTrigger restored to " +
                    $"'{saved.OriginalValue}'"
                );

                Plugin.Logger.LogInfo(
                    $"{saved.LogName} post-restore unfocusAnimationTrigger=" +
                    $"'{saved.Field.GetValue(saved.Panel)}'"
                );
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogError(
                    $"Failed to restore {saved.LogName} " +
                    $"unfocusAnimationTrigger: {exception}"
                );
            }
        }

        SavedUnfocusTriggers.Clear();
    }

    public static void HandleDynamicEpilogueSubtitle(
        Subtitle subtitle)
    {
        try
        {
            DialogueEntry entry =
                subtitle?.dialogueEntry;

            if (entry == null)
            {
                return;
            }

            if (!FocusPanelsByEntry.TryGetValue(
                    entry.conversationID,
                    out Dictionary<int, int[]> titleEntries) ||
                !titleEntries.TryGetValue(
                    entry.id,
                    out int[] panels) ||
                panels.Length == 0)
            {
                CheckFocusPersistence(
                    entry
                );
                return;
            }

            titleEntries.Remove(entry.id);

            GameObject dialogueUIGameObject =
                GetDialogueUIGameObject(
                    DialogueManager.dialogueUI
                );

            if (dialogueUIGameObject == null)
            {
                Plugin.Logger.LogWarning(
                    "Could not focus dynamic epilogue portrait panels: " +
                    "DialogueManager.dialogueUI GameObject is unavailable."
                );
                return;
            }

            Component[] subtitlePanels =
                dialogueUIGameObject
                    .GetComponentsInChildren<Component>(true)
                    .Where(component =>
                        component != null &&
                        string.Equals(
                            component.GetType().FullName,
                            WrapperSubtitlePanelTypeName,
                            StringComparison.Ordinal))
                    .ToArray();

            var focusedNames =
                new List<string>();

            Plugin.Logger.LogInfo(
                "Applying FocusNow for epilogue section: " +
                GetEntrySectionName(entry)
            );

            foreach (int panel in panels)
            {
                string panelName =
                    GetPortraitPanelName(panel);

                if (panelName == null)
                {
                    Plugin.Logger.LogWarning(
                        $"Could not focus dynamic epilogue portrait panel {panel}: " +
                        "no mapped portrait panel name."
                    );
                    continue;
                }

                Component subtitlePanel =
                    subtitlePanels.FirstOrDefault(component =>
                        string.Equals(
                            component.gameObject.name,
                            panelName,
                            StringComparison.Ordinal));

                if (subtitlePanel == null)
                {
                    Plugin.Logger.LogWarning(
                        $"Could not focus dynamic epilogue portrait panel {panel}: " +
                        $"{panelName} was not found."
                    );
                    continue;
                }

                if (InvokeFocusNow(
                        subtitlePanel,
                        panelName,
                        panel))
                {
                    focusedNames.Add(panelName);
                }
            }

            Plugin.Logger.LogInfo(
                "Dynamic epilogue portrait focus: " +
                $"section={GetEntrySectionName(entry)} " +
                $"mode={(panels.Length == 1 ? "one-person" : "two-person")} " +
                $"panels={string.Join(", ", focusedNames)}"
            );
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError(
                $"Failed to focus dynamic epilogue portrait panels: {exception}"
            );
        }
    }

    private static void CheckFocusPersistence(
        DialogueEntry entry)
    {
        if (loggedFocusPersistenceCheck)
        {
            return;
        }

        if (!FocusPersistenceCheckByEntry.TryGetValue(
                entry.conversationID,
                out Dictionary<int, int[]> entries) ||
            !entries.TryGetValue(
                entry.id,
                out int[] panels) ||
            panels.Length == 0)
        {
            return;
        }

        loggedFocusPersistenceCheck = true;
        entries.Remove(entry.id);

        GameObject dialogueUIGameObject =
            GetDialogueUIGameObject(
                DialogueManager.dialogueUI
            );

        if (dialogueUIGameObject == null)
        {
            Plugin.Logger.LogWarning(
                "Post-title focus persistence check skipped: " +
                "DialogueManager.dialogueUI GameObject is unavailable."
            );
            return;
        }

        Component[] subtitlePanels =
            dialogueUIGameObject
                .GetComponentsInChildren<Component>(true)
                .Where(component =>
                    component != null &&
                    string.Equals(
                        component.GetType().FullName,
                        WrapperSubtitlePanelTypeName,
                        StringComparison.Ordinal))
                .ToArray();

        Plugin.Logger.LogInfo(
            "Post-title focus persistence check:"
        );

        foreach (int panel in panels)
        {
            string panelName =
                GetPortraitPanelName(panel);

            Component subtitlePanel =
                subtitlePanels.FirstOrDefault(component =>
                    string.Equals(
                        component.gameObject.name,
                        panelName,
                        StringComparison.Ordinal));

            LogFocusState(
                subtitlePanel,
                panelName,
                panel
            );
        }
    }

    private static string GetPortraitPanelName(
        int panel)
    {
        switch (panel)
        {
            case 0:
                return FarLeftPanelName;
            case 3:
                return FarRightPanelName;
            default:
                return null;
        }
    }

    private static string GetEntrySectionName(
        DialogueEntry entry)
    {
        const string narratorPanelTag =
            "[panel=1]";

        string text =
            entry.DialogueText ?? string.Empty;

        if (text.StartsWith(
                narratorPanelTag,
                StringComparison.Ordinal))
        {
            text =
                text.Substring(narratorPanelTag.Length);
        }

        return string.IsNullOrWhiteSpace(text)
            ? $"entry {entry.id}"
            : text;
    }

    private static bool InvokeFocusNow(
        Component subtitlePanel,
        string panelName,
        int panelNumber)
    {
        try
        {
            MethodInfo focusMethod =
                FindInstanceMethod(
                    subtitlePanel.GetType(),
                    "FocusNow"
                );

            if (focusMethod == null)
            {
                Plugin.Logger.LogWarning(
                    $"Could not focus {panelName}: FocusNow() was not found on " +
                    $"{subtitlePanel.GetType().FullName}."
                );
                return false;
            }

            focusMethod.Invoke(
                subtitlePanel,
                null
            );

            Plugin.Logger.LogInfo(
                $"FocusNow invoked: {panelName}"
            );

            LogFocusState(
                subtitlePanel,
                panelName,
                panelNumber
            );

            return true;
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"Could not FocusNow {panelName} on " +
                $"{subtitlePanel.GetType().FullName}: {exception}"
            );
            return false;
        }
    }

    private static void LogFocusState(
        Component subtitlePanel,
        string panelName,
        int panelNumber)
    {
        if (subtitlePanel == null)
        {
            Plugin.Logger.LogInfo(
                $"  panel='{panelName}' panelNumber={panelNumber} component=NULL"
            );
            return;
        }

        Plugin.Logger.LogInfo(
            $"  panel='{panelName}' " +
            $"panelNumber={panelNumber} " +
            $"hasFocus={ReadMemberValue(subtitlePanel, "hasFocus")} " +
            $"m_hasFocus={ReadMemberValue(subtitlePanel, "m_hasFocus")} " +
            $"panelState={ReadMemberValue(subtitlePanel, "panelState")} " +
            $"currentTrigger={ReadAnimatorMonitorCurrentTrigger(subtitlePanel)} " +
            $"unfocusAnimationTrigger='{ReadMemberValue(subtitlePanel, "unfocusAnimationTrigger")}'"
        );
    }

    private static string ReadMemberValue(
        object instance,
        string memberName)
    {
        if (instance == null)
        {
            return "NULL";
        }

        for (Type type = instance.GetType();
             type != null;
             type = type.BaseType)
        {
            FieldInfo field =
                type.GetField(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            if (field != null)
            {
                return FormatReflectedValue(
                    SafeGetFieldValue(
                        field,
                        instance
                    )
                );
            }

            PropertyInfo property =
                type.GetProperty(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            if (property != null &&
                property.CanRead &&
                property.GetIndexParameters().Length == 0)
            {
                return FormatReflectedValue(
                    SafeGetPropertyValue(
                        property,
                        instance
                    )
                );
            }
        }

        return "<missing>";
    }

    private static string ReadAnimatorMonitorCurrentTrigger(
        Component subtitlePanel)
    {
        object animatorMonitor =
            ReadRawMemberValue(
                subtitlePanel,
                "animatorMonitor"
            );

        return ReadMemberValue(
            animatorMonitor,
            "currentTrigger"
        );
    }

    private static object ReadRawMemberValue(
        object instance,
        string memberName)
    {
        if (instance == null)
        {
            return null;
        }

        for (Type type = instance.GetType();
             type != null;
             type = type.BaseType)
        {
            FieldInfo field =
                type.GetField(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            if (field != null)
            {
                return SafeGetFieldValue(
                    field,
                    instance
                );
            }

            PropertyInfo property =
                type.GetProperty(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            if (property != null &&
                property.CanRead &&
                property.GetIndexParameters().Length == 0)
            {
                return SafeGetPropertyValue(
                    property,
                    instance
                );
            }
        }

        return null;
    }

    private static MethodInfo FindInstanceMethod(
        Type type,
        string methodName)
    {
        while (type != null)
        {
            MethodInfo method =
                type.GetMethod(
                    methodName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null
                );

            if (method != null)
            {
                return method;
            }

            type =
                type.BaseType;
        }

        return null;
    }

    private static FieldInfo FindInstanceField(
        Type type,
        string fieldName)
    {
        while (type != null)
        {
            FieldInfo field =
                type.GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            if (field != null)
            {
                return field;
            }

            type =
                type.BaseType;
        }

        return null;
    }

    private static void DumpTransitionInspectionOnce(
        string phase,
        Component[] subtitlePanels)
    {
        if (!LoggedTransitionInspections.Add(phase))
        {
            return;
        }

        try
        {
            Plugin.Logger.LogInfo(
                $"----- StandardUISubtitlePanel transition inspection: {phase} -----"
            );

            Component farLeft =
                FindPanelComponent(
                    subtitlePanels,
                    FarLeftPanelName
                );

            Component farRight =
                FindPanelComponent(
                    subtitlePanels,
                    FarRightPanelName
                );

            Component centerLeft =
                FindPanelComponent(
                    subtitlePanels,
                    CenterLeftPanelName
                );

            DumpTransitionPanel(
                "Far Left",
                farLeft
            );

            DumpTransitionPanel(
                "Far Right",
                farRight
            );

            DumpTransitionPanel(
                "Center Left narrator panel",
                centerLeft
            );

            string[] farLeftSignature =
                BuildTransitionSignature(farLeft);

            string[] farRightSignature =
                BuildTransitionSignature(farRight);

            string[] centerLeftSignature =
                BuildTransitionSignature(centerLeft);

            Plugin.Logger.LogInfo(
                "Far Left and Far Right transition signatures match: " +
                farLeftSignature.SequenceEqual(farRightSignature)
            );

            Plugin.Logger.LogInfo(
                "Center Left narrator transition signature differs from Far Left: " +
                !centerLeftSignature.SequenceEqual(farLeftSignature)
            );

            Plugin.Logger.LogInfo(
                "----- End transition inspection -----"
            );
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError(
                $"Failed StandardUISubtitlePanel transition inspection: {exception}"
            );
        }
    }

    private static Component FindPanelComponent(
        Component[] subtitlePanels,
        string panelName)
    {
        return subtitlePanels?.FirstOrDefault(component =>
            component != null &&
            string.Equals(
                component.gameObject.name,
                panelName,
                StringComparison.Ordinal));
    }

    private static void DumpTransitionPanel(
        string label,
        Component panel)
    {
        if (panel == null)
        {
            Plugin.Logger.LogInfo(
                $"{label}: panel component not found."
            );
            return;
        }

        Type type =
            panel.GetType();

        Plugin.Logger.LogInfo(
            $"{label}: GameObject='{panel.gameObject.name}' Runtime type={type.FullName}"
        );

        DumpInheritanceChain(
            type
        );

        var visited =
            new HashSet<object>(
                ReferenceEqualityComparer.Instance
            );

        DumpTransitionMembers(
            panel,
            type,
            0,
            visited
        );
    }

    private static void DumpInheritanceChain(
        Type type)
    {
        for (Type current = type;
             current != null;
             current = current.BaseType)
        {
            Plugin.Logger.LogInfo(
                $"Inheritance chain: type={current.FullName} " +
                $"base={current.BaseType?.FullName ?? "NULL"}"
            );
        }
    }

    private static void DumpTransitionMembers(
        object instance,
        Type type,
        int depth,
        HashSet<object> visited)
    {
        if (instance == null ||
            type == null ||
            depth > 1 ||
            !visited.Add(instance))
        {
            return;
        }

        for (Type current = type;
             current != null;
             current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (!IsTransitionInspectionMember(field.Name))
                {
                    continue;
                }

                object value =
                    SafeGetFieldValue(
                        field,
                        instance
                    );

                Plugin.Logger.LogInfo(
                    $"Relevant field/property: DeclaringType={field.DeclaringType?.FullName} " +
                    $"Kind=Field Name={field.Name} Type={field.FieldType.FullName} " +
                    $"Value={FormatReflectedValue(value)}"
                );

                MaybeDumpNestedTransitionObject(
                    field.Name,
                    value,
                    depth,
                    visited
                );
            }

            foreach (PropertyInfo property in current.GetProperties(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (!IsTransitionInspectionMember(property.Name) ||
                    property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object value =
                    property.CanRead
                        ? SafeGetPropertyValue(
                            property,
                            instance)
                        : null;

                Plugin.Logger.LogInfo(
                    $"Relevant field/property: DeclaringType={property.DeclaringType?.FullName} " +
                    $"Kind=Property Name={property.Name} Type={property.PropertyType.FullName} " +
                    $"CanRead={property.CanRead} CanWrite={property.CanWrite} " +
                    $"Value={FormatReflectedValue(value)}"
                );

                MaybeDumpNestedTransitionObject(
                    property.Name,
                    value,
                    depth,
                    visited
                );
            }

            foreach (MethodInfo method in current.GetMethods(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (!IsTransitionInspectionMethod(method.Name))
                {
                    continue;
                }

                Plugin.Logger.LogInfo(
                    $"Relevant method: DeclaringType={method.DeclaringType?.FullName} " +
                    $"Name={method.Name} Parameters=({FormatParameters(method)}) " +
                    $"ReturnType={method.ReturnType.FullName}"
                );
            }
        }
    }

    private static void MaybeDumpNestedTransitionObject(
        string memberName,
        object value,
        int depth,
        HashSet<object> visited)
    {
        if (value == null ||
            depth >= 1 ||
            value is string ||
            value is UnityEngine.Object ||
            value.GetType().IsPrimitive ||
            value.GetType().IsEnum)
        {
            return;
        }

        Type type =
            value.GetType();

        if (!IsTransitionLikeObject(
                memberName,
                type))
        {
            return;
        }

        Plugin.Logger.LogInfo(
            $"Nested transition object: Member={memberName} Type={type.FullName}"
        );

        DumpTransitionMembers(
            value,
            type,
            depth + 1,
            visited
        );
    }

    private static string[] BuildTransitionSignature(
        Component panel)
    {
        if (panel == null)
        {
            return Array.Empty<string>();
        }

        var values =
            new List<string>();

        for (Type current = panel.GetType();
             current != null;
             current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (!IsTransitionInspectionMember(field.Name))
                {
                    continue;
                }

                values.Add(
                    $"F:{field.DeclaringType?.FullName}.{field.Name}=" +
                    $"{FormatReflectedValue(SafeGetFieldValue(field, panel))}"
                );
            }

            foreach (PropertyInfo property in current.GetProperties(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (!IsTransitionInspectionMember(property.Name) ||
                    property.GetIndexParameters().Length > 0 ||
                    !property.CanRead)
                {
                    continue;
                }

                values.Add(
                    $"P:{property.DeclaringType?.FullName}.{property.Name}=" +
                    $"{FormatReflectedValue(SafeGetPropertyValue(property, panel))}"
                );
            }
        }

        return values
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static object SafeGetFieldValue(
        FieldInfo field,
        object instance)
    {
        try
        {
            return field.GetValue(instance);
        }
        catch (Exception exception)
        {
            return "<unreadable: " + exception.GetType().Name + ">";
        }
    }

    private static object SafeGetPropertyValue(
        PropertyInfo property,
        object instance)
    {
        try
        {
            return property.GetValue(instance, null);
        }
        catch (Exception exception)
        {
            return "<unreadable: " + exception.GetType().Name + ">";
        }
    }

    private static bool IsTransitionLikeObject(
        string memberName,
        Type type)
    {
        return IsTransitionInspectionMember(memberName) ||
               ContainsIgnoreCase(type.FullName, "transition") ||
               ContainsIgnoreCase(type.FullName, "animation") ||
               ContainsIgnoreCase(type.FullName, "animator") ||
               ContainsIgnoreCase(type.FullName, "showhide") ||
               ContainsIgnoreCase(type.FullName, "trigger");
    }

    private static bool IsTransitionInspectionMember(
        string name)
    {
        return ContainsIgnoreCase(name, "focus") ||
               ContainsIgnoreCase(name, "unfocus") ||
               ContainsIgnoreCase(name, "animation") ||
               ContainsIgnoreCase(name, "animator") ||
               ContainsIgnoreCase(name, "trigger") ||
               ContainsIgnoreCase(name, "transition") ||
               ContainsIgnoreCase(name, "show") ||
               ContainsIgnoreCase(name, "hide") ||
               ContainsIgnoreCase(name, "panel") ||
               ContainsIgnoreCase(name, "ui");
    }

    private static bool IsTransitionInspectionMethod(
        string name)
    {
        return ContainsIgnoreCase(name, "focus") ||
               ContainsIgnoreCase(name, "unfocus") ||
               ContainsIgnoreCase(name, "animation") ||
               ContainsIgnoreCase(name, "trigger") ||
               ContainsIgnoreCase(name, "transition") ||
               ContainsIgnoreCase(name, "show") ||
               ContainsIgnoreCase(name, "hide");
    }

    private sealed class ReferenceEqualityComparer :
        IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance =
            new();

        public new bool Equals(
            object x,
            object y)
        {
            return ReferenceEquals(
                x,
                y
            );
        }

        public int GetHashCode(
            object obj)
        {
            return RuntimeHelpers.GetHashCode(
                obj
            );
        }
    }

    public static void MaybeDumpSubtitlePanelSnapshot(
        Subtitle subtitle)
    {
        try
        {
            DialogueEntry entry =
                subtitle?.dialogueEntry;

            if (entry == null ||
                !FocusPanelsByEntry.ContainsKey(entry.conversationID))
            {
                return;
            }

            string text =
                entry.DialogueText ?? string.Empty;

            string sequence =
                entry.Sequence ?? string.Empty;

            string label = null;

            if (entry.ActorID == 18 &&
                text.StartsWith("[panel=0]", StringComparison.Ordinal) &&
                sequence.Contains("SetPanel(18, 0, immediate)"))
            {
                label =
                    "after Slyker panel 0 setup";
            }
            else if (entry.ActorID == 19 &&
                     text.StartsWith("[panel=3]", StringComparison.Ordinal) &&
                     sequence.Contains("SetPanel(19, 3, immediate)"))
            {
                label =
                    "after Marcus panel 3 setup";
            }
            else if (entry.ActorID == NarratorActorId &&
                     entry.ConversantID == NarratorActorId &&
                     text.StartsWith(
                         "[panel=1]Slyker & Marcus -- The Ones Who Stayed",
                         StringComparison.Ordinal))
            {
                label =
                    "after narrator panel 1 title starts";
            }

            if (label == null ||
                !LoggedSubtitlePanelSnapshots.Add(label))
            {
                return;
            }

            DumpSubtitlePanelSnapshot(
                label
            );
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError(
                $"Failed to dump subtitle panel snapshot: {exception}"
            );
        }
    }

    private static void DumpSubtitlePanelSnapshot(
        string label)
    {
        GameObject dialogueUIGameObject =
            GetDialogueUIGameObject(
                DialogueManager.dialogueUI
            );

        Plugin.Logger.LogInfo(
            $"----- Subtitle panel snapshot: {label} -----"
        );

        if (dialogueUIGameObject == null)
        {
            Plugin.Logger.LogInfo(
                "Dialogue UI GameObject is NULL."
            );
            Plugin.Logger.LogInfo(
                "----- End snapshot -----"
            );
            return;
        }

        Component[] panels =
            dialogueUIGameObject
                .GetComponentsInChildren<Component>(true)
                .Where(component =>
                    component != null &&
                    string.Equals(
                        component.GetType().FullName,
                        "PixelCrushers.DialogueSystem.Wrappers.StandardUISubtitlePanel",
                        StringComparison.Ordinal))
                .ToArray();

        Plugin.Logger.LogInfo(
            $"StandardUISubtitlePanel count: {panels.Length}"
        );

        foreach (Component panel in panels)
        {
            DumpSubtitlePanelComponent(
                panel
            );
        }

        Plugin.Logger.LogInfo(
            "Observed mapping: inspect which panel GameObject changes " +
            "after logical panel 0, panel 3, and panel 1 entries."
        );

        Plugin.Logger.LogInfo(
            "----- End snapshot -----"
        );
    }

    private static void DumpSubtitlePanelComponent(
        Component panel)
    {
        GameObject gameObject =
            panel.gameObject;

        Transform transform =
            panel.transform;

        RectTransform rectTransform =
            transform as RectTransform;

        Plugin.Logger.LogInfo(
            $"Name='{gameObject.name}' " +
            $"activeSelf={gameObject.activeSelf} " +
            $"activeInHierarchy={gameObject.activeInHierarchy} " +
            $"instanceID={gameObject.GetInstanceID()} " +
            $"siblingIndex={transform.GetSiblingIndex()} " +
            $"parent='{(transform.parent == null ? "NULL" : transform.parent.name)}' " +
            $"localPosition={FormatVector3(transform.localPosition)} " +
            $"anchoredPosition={(rectTransform == null ? "N/A" : FormatVector2(rectTransform.anchoredPosition))}"
        );

        DumpPanelImages(
            gameObject
        );

        DumpPanelReflectionHints(
            panel
        );
    }

    private static void DumpPanelImages(
        GameObject panelGameObject)
    {
        Component[] images =
            panelGameObject
                .GetComponentsInChildren<Component>(true)
                .Where(component =>
                    component != null &&
                    string.Equals(
                        component.GetType().FullName,
                        "UnityEngine.UI.Image",
                        StringComparison.Ordinal))
                .ToArray();

        bool foundCandidate = false;

        foreach (Component image in images)
        {
            if (image == null)
            {
                continue;
            }

            string name =
                image.gameObject.name;

            bool likelyPortrait =
                ContainsIgnoreCase(name, "portrait") ||
                ContainsIgnoreCase(name, "image") ||
                ContainsIgnoreCase(name, "speaker") ||
                ContainsIgnoreCase(name, "actor") ||
                ContainsIgnoreCase(name, "character");

            bool shouldLog =
                likelyPortrait ||
                (GetBoolProperty(image, "enabled") &&
                 image.gameObject.activeInHierarchy);

            if (!shouldLog)
            {
                continue;
            }

            foundCandidate = true;

            Plugin.Logger.LogInfo(
                $"  Image='{name}' " +
                $"enabled={GetBoolProperty(image, "enabled")} " +
                $"activeInHierarchy={image.gameObject.activeInHierarchy} " +
                $"sprite={GetSpriteName(image)} " +
                $"alpha={GetImageAlpha(image)} " +
                $"likelyPortrait={likelyPortrait}"
            );
        }

        Plugin.Logger.LogInfo(
            $"  visiblePortraitCandidate={foundCandidate}"
        );
    }

    private static bool GetBoolProperty(
        object target,
        string propertyName)
    {
        try
        {
            object value =
                target.GetType()
                    .GetProperty(
                        propertyName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    ?.GetValue(target, null);

            return value is bool boolean &&
                   boolean;
        }
        catch
        {
            return false;
        }
    }

    private static string GetSpriteName(
        object image)
    {
        try
        {
            object sprite =
                image.GetType()
                    .GetProperty(
                        "sprite",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    ?.GetValue(image, null);

            return sprite is UnityEngine.Object unityObject
                ? unityObject.name
                : "NULL";
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string GetImageAlpha(
        object image)
    {
        try
        {
            object color =
                image.GetType()
                    .GetProperty(
                        "color",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    ?.GetValue(image, null);

            if (color is Color unityColor)
            {
                return unityColor.a.ToString("0.###");
            }

            return "N/A";
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static void DumpPanelReflectionHints(
        Component panel)
    {
        Type type =
            panel.GetType();

        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            if (!IsPanelSnapshotMember(field.Name))
            {
                continue;
            }

            Plugin.Logger.LogInfo(
                $"  field {field.Name}={FormatReflectedValue(field.GetValue(panel))}"
            );
        }

        foreach (PropertyInfo property in type.GetProperties(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            if (!IsPanelSnapshotMember(property.Name) ||
                property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            try
            {
                Plugin.Logger.LogInfo(
                    $"  property {property.Name}=" +
                    $"{FormatReflectedValue(property.GetValue(panel, null))}"
                );
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogInfo(
                    $"  property {property.Name}=<unreadable: " +
                    $"{exception.GetType().Name}>"
                );
            }
        }

        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            if (!IsPanelSnapshotMember(method.Name))
            {
                continue;
            }

            Plugin.Logger.LogInfo(
                $"  method {method.Name}({FormatParameters(method)})"
            );
        }
    }

    private static bool IsPanelSnapshotMember(
        string name)
    {
        return ContainsIgnoreCase(name, "focus") ||
               ContainsIgnoreCase(name, "unfocus") ||
               ContainsIgnoreCase(name, "panel") ||
               ContainsIgnoreCase(name, "portrait") ||
               ContainsIgnoreCase(name, "actor") ||
               ContainsIgnoreCase(name, "speaker") ||
               ContainsIgnoreCase(name, "subtitle") ||
               ContainsIgnoreCase(name, "image") ||
               ContainsIgnoreCase(name, "getpanel");
    }

    private static bool ContainsIgnoreCase(
        string value,
        string search)
    {
        return value != null &&
               value.IndexOf(
                   search,
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FormatReflectedValue(
        object value)
    {
        if (value == null)
        {
            return "NULL";
        }

        Type type =
            value.GetType();

        if (type.IsPrimitive ||
            type.IsEnum ||
            value is string)
        {
            return value.ToString();
        }

        if (value is UnityEngine.Object unityObject)
        {
            return $"{type.FullName}('{unityObject.name}')";
        }

        return type.FullName;
    }

    private static string FormatVector2(
        Vector2 value)
    {
        return $"({value.x:0.###}, {value.y:0.###})";
    }

    private static string FormatVector3(
        Vector3 value)
    {
        return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
    }

    private static int GetActorId(
        string character)
    {
        if (ActorIds.TryGetValue(character, out int actorId))
        {
            return actorId;
        }

        Plugin.Logger.LogError(
            $"Unknown dynamic epilogue actor name: {character}."
        );
        return NarratorActorId;
    }

    private static bool IsCharacterAlive(
        string character,
        out int? vitality)
    {
        vitality = null;

        string key =
            character +
            "0CurrentVitality";

        try
        {
            if (!SaveController.KeyExists(key))
            {
                return false;
            }

            int value =
                SaveController.Load<int>(key, 0);

            vitality = value;
            return value > 0;
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError(
                $"Failed to read vitality key {key}: {exception.Message}"
            );
            return false;
        }
    }

    private static string GetTitle(
        EpilogueRoute route)
    {
        return route == EpilogueRoute.Revolution
            ? RevolutionTitle
            : ReformTitle;
    }

    private static bool IsChapter20EndingConversation(
        string conversationId)
    {
        return !string.IsNullOrEmpty(conversationId) &&
               (conversationId.StartsWith(
                    "Chapter20A/End/",
                    StringComparison.Ordinal) ||
                conversationId.StartsWith(
                    "Chapter20R/End/",
                    StringComparison.Ordinal));
    }

    private static int CountEligibleEpilogues(
        EpilogueSection[] sections)
    {
        int count = 0;

        foreach (EpilogueSection section in sections)
        {
            if (section.Kind != EpilogueKind.Epilogue)
            {
                continue;
            }

            if (section.Characters.All(character =>
                    IsCharacterAlive(character, out _)))
            {
                count++;
            }
        }

        return count;
    }

    private static bool ValidateAnchors()
    {
        if (anchorsValidated)
        {
            return anchorsValid;
        }

        anchorsValidated = true;

        try
        {
            MethodInfo victory =
                AccessTools.Method(
                    typeof(Chapter20Controller),
                    "Victory"
                );

            MethodBase moveNext =
                GetIteratorMoveNext(victory);

            if (moveNext == null)
            {
                Plugin.Logger.LogError(
                    "Could not locate Chapter20Controller.Victory MoveNext; " +
                    "dynamic epilogue wrapper disabled."
                );
                anchorsValid = false;
                return false;
            }

            Plugin.Logger.LogInfo(
                $"DynamicEpilogues state-machine method: " +
                $"{moveNext.DeclaringType?.FullName}.{moveNext.Name}"
            );

            int revolutionCount =
                CountLdstr(moveNext, RevolutionAnchor);

            int reformCount =
                CountLdstr(moveNext, ReformAnchor);

            Plugin.Logger.LogInfo(
                $"Reform anchor count: {reformCount}"
            );

            Plugin.Logger.LogInfo(
                $"Revolution anchor count: {revolutionCount}"
            );

            if (revolutionCount != 1 ||
                reformCount != 1)
            {
                Plugin.Logger.LogError(
                    "Dynamic epilogue anchor validation failed. " +
                    $"{RevolutionAnchor} count={revolutionCount}, " +
                    $"{ReformAnchor} count={reformCount}. " +
                    "Leaving vanilla Victory coroutine unmodified."
                );
                anchorsValid = false;
                return false;
            }

            anchorsValid = true;
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError(
                $"Dynamic epilogue anchor validation failed: {exception}"
            );
            anchorsValid = false;
            return false;
        }
    }

    private static MethodBase GetIteratorMoveNext(
        MethodInfo iteratorMethod)
    {
        if (iteratorMethod == null)
        {
            return null;
        }

        var attribute =
            iteratorMethod.GetCustomAttribute<IteratorStateMachineAttribute>();

        Type stateMachineType =
            attribute?.StateMachineType;

        if (stateMachineType == null)
        {
            stateMachineType =
                typeof(Chapter20Controller)
                    .GetNestedTypes(
                        BindingFlags.NonPublic |
                        BindingFlags.Public
                    )
                    .FirstOrDefault(type =>
                        type.Name.Contains("Victory"));
        }

        return stateMachineType?.GetMethod(
            "MoveNext",
            BindingFlags.Instance |
            BindingFlags.NonPublic |
            BindingFlags.Public
        );
    }

    private static int CountLdstr(
        MethodBase method,
        string value)
    {
        byte[] il =
            method.GetMethodBody()?.GetILAsByteArray();

        if (il == null)
        {
            return 0;
        }

        int count = 0;
        int offset = 0;
        Module module =
            method.Module;

        while (offset < il.Length)
        {
            OpCode opCode =
                ReadOpCode(il, ref offset);

            int operandSize =
                GetOperandSize(opCode, il, offset);

            if (opCode == OpCodes.Ldstr &&
                operandSize == 4)
            {
                int token =
                    BitConverter.ToInt32(il, offset);

                string resolved =
                    module.ResolveString(token);

                if (string.Equals(
                        resolved,
                        value,
                        StringComparison.Ordinal))
                {
                    count++;
                }
            }

            offset += operandSize;
        }

        return count;
    }

    private static OpCode ReadOpCode(
        byte[] il,
        ref int offset)
    {
        ushort value = il[offset++];

        if (value != 0xFE)
        {
            return SingleByteOpCodes[value];
        }

        value = il[offset++];
        return MultiByteOpCodes[value];
    }

    private static int GetOperandSize(
        OpCode opCode,
        byte[] il,
        int offset)
    {
        switch (opCode.OperandType)
        {
            case OperandType.InlineNone:
                return 0;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                return 1;
            case OperandType.InlineVar:
                return 2;
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                return 4;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                return 8;
            case OperandType.InlineSwitch:
                int cases =
                    BitConverter.ToInt32(il, offset);
                return 4 + cases * 4;
            default:
                return 0;
        }
    }

    private static string ReadEmbeddedText(
        string resourceName)
    {
        Assembly assembly =
            typeof(DynamicEpilogueRuntime).Assembly;

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            throw new InvalidOperationException(
                $"Embedded epilogue resource not found: {resourceName}"
            );
        }

        using var reader =
            new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private sealed class RuntimeState
    {
        public static readonly RuntimeState Conflict =
            new(null, null);

        public Conversation RevolutionConversation { get; }
        public Conversation ReformConversation { get; }
        public bool CanUseConversations =>
            RevolutionConversation != null &&
            ReformConversation != null;

        public RuntimeState(
            Conversation revolutionConversation,
            Conversation reformConversation)
        {
            RevolutionConversation = revolutionConversation;
            ReformConversation = reformConversation;
        }

        public bool IsStillRegisteredIn(
            DialogueDatabase database)
        {
            if (!CanUseConversations)
            {
                return true;
            }

            return database.conversations.Contains(RevolutionConversation) &&
                   database.conversations.Contains(ReformConversation);
        }
    }

    private sealed class SavedUnfocusTrigger
    {
        public Component Panel { get; }
        public FieldInfo Field { get; }
        public string OriginalValue { get; }
        public string LogName { get; }

        public SavedUnfocusTrigger(
            Component panel,
            FieldInfo field,
            string originalValue,
            string logName)
        {
            Panel = panel;
            Field = field;
            OriginalValue = originalValue;
            LogName = logName;
        }
    }

    private readonly struct EntrySpec
    {
        public string ActorName { get; }
        public int ActorId { get; }
        public int ConversantId { get; }
        public string Text { get; }
        public string Sequence { get; }
        public bool FocusOnce { get; }
        public bool CheckFocusPersistence { get; }
        public int[] FocusPanels { get; }

        public EntrySpec(
            string actorName,
            int actorId,
            int conversantId,
            string text,
            string sequence = "",
            bool focusOnce = false,
            bool checkFocusPersistence = false,
            params int[] focusPanels)
        {
            ActorName = actorName;
            ActorId = actorId;
            ConversantId = conversantId;
            Text = text;
            Sequence = sequence;
            FocusOnce = focusOnce;
            CheckFocusPersistence = checkFocusPersistence;
            FocusPanels =
                focusPanels ?? Array.Empty<int>();
        }
    }

    private sealed class EpilogueSection
    {
        public EpilogueKind Kind { get; }
        public string[] Characters { get; }
        public string Title { get; }
        public string[] Paragraphs { get; }

        public EpilogueSection(
            EpilogueKind kind,
            string[] characters,
            string title,
            string[] paragraphs)
        {
            Kind = kind;
            Characters = characters;
            Title = title;
            Paragraphs = paragraphs;
        }
    }

    private enum EpilogueKind
    {
        Introduction,
        Epilogue,
        End
    }

    private static class EpilogueParser
    {
        public static EpilogueSection[] Parse(
            string text,
            string routeName)
        {
            string[] lines =
                text.Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Split('\n');

            var sections =
                new List<EpilogueSection>();

            var introParagraphs =
                new List<string>();

            EpilogueDraft current = null;

            foreach (string rawLine in lines)
            {
                string line =
                    rawLine.Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith(
                        "All Epilogues ",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        line,
                        "Narrator",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(
                        line,
                        "THE END",
                        StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrent(
                        sections,
                        current
                    );
                    current = null;

                    sections.Add(
                        new EpilogueSection(
                            EpilogueKind.End,
                            Array.Empty<string>(),
                            "THE END",
                            Array.Empty<string>()
                        )
                    );
                    continue;
                }

                if (TryParseHeader(
                        line,
                        out string[] characters,
                        out string title))
                {
                    if (current == null &&
                        introParagraphs.Count > 0)
                    {
                        sections.Add(
                            new EpilogueSection(
                                EpilogueKind.Introduction,
                                Array.Empty<string>(),
                                string.Empty,
                                introParagraphs.ToArray()
                            )
                        );
                        introParagraphs.Clear();
                    }

                    FlushCurrent(
                        sections,
                        current
                    );

                    current =
                        new EpilogueDraft(
                            characters,
                            title
                        );
                    continue;
                }

                if (current == null)
                {
                    introParagraphs.Add(line);
                }
                else
                {
                    current.Paragraphs.Add(line);
                }
            }

            if (current == null &&
                introParagraphs.Count > 0)
            {
                sections.Add(
                    new EpilogueSection(
                        EpilogueKind.Introduction,
                        Array.Empty<string>(),
                        string.Empty,
                        introParagraphs.ToArray()
                    )
                );
            }

            FlushCurrent(
                sections,
                current
            );

            Plugin.Logger?.LogInfo(
                $"{routeName} dynamic epilogue parser loaded " +
                $"{sections.Count(section => section.Kind == EpilogueKind.Epilogue)} " +
                "character epilogue sections."
            );

            return sections.ToArray();
        }

        private static bool TryParseHeader(
            string line,
            out string[] characters,
            out string title)
        {
            characters = null;
            title = null;

            int separator =
                line.IndexOf(
                    " - ",
                    StringComparison.Ordinal
                );

            if (separator <= 0)
            {
                return false;
            }

            string characterText =
                line.Substring(0, separator);

            string[] parsedCharacters =
                characterText
                    .Split('&')
                    .Select(character => character.Trim())
                    .Where(character => character.Length > 0)
                    .ToArray();

            if (parsedCharacters.Length == 0 ||
                parsedCharacters.Length > 2)
            {
                return false;
            }

            if (parsedCharacters.Any(character =>
                    !ActorIds.ContainsKey(character)))
            {
                return false;
            }

            characters = parsedCharacters;
            title = line.Substring(separator + 3).Trim();

            return title.Length > 0;
        }

        private static void FlushCurrent(
            List<EpilogueSection> sections,
            EpilogueDraft current)
        {
            if (current == null)
            {
                return;
            }

            sections.Add(
                new EpilogueSection(
                    EpilogueKind.Epilogue,
                    current.Characters,
                    current.Title,
                    current.Paragraphs.ToArray()
                )
            );
        }

        private sealed class EpilogueDraft
        {
            public string[] Characters { get; }
            public string Title { get; }
            public List<string> Paragraphs { get; } =
                new();

            public EpilogueDraft(
                string[] characters,
                string title)
            {
                Characters = characters;
                Title = title;
            }
        }
    }
}

[HarmonyPatch]
internal static class ChapterControllerStartConversationPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        List<MethodInfo> methods =
            typeof(ChapterController)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                )
                .Where(method =>
                {
                    if (!string.Equals(
                            method.Name,
                            "StartConversation",
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    ParameterInfo[] parameters =
                        method.GetParameters();

                    return parameters.Length > 0 &&
                           parameters[0].ParameterType == typeof(string);
                })
                .ToList();

        foreach (MethodInfo method in methods)
        {
            Plugin.Logger?.LogInfo(
                $"StartConversation observer target resolved: " +
                $"{method.DeclaringType?.FullName}.{method.Name}"
            );
        }

        if (methods.Count == 0)
        {
            Plugin.Logger?.LogError(
                "No ChapterController.StartConversation observer targets resolved."
            );
        }

        return methods;
    }

    private static bool Prefix(
        ChapterController __instance,
        string conversationId)
    {
        return DynamicEpilogueRuntime.NoteConversationStart(
            __instance,
            conversationId
        );
    }
}

[HarmonyPatch]
internal static class Chapter20VictoryPatch
{
    private static MethodBase TargetMethod()
    {
        Plugin.Logger?.LogInfo(
            "Resolving DynamicEpilogues target method..."
        );

        MethodInfo method =
            AccessTools.Method(
                typeof(Chapter20Controller),
                "Victory"
            );

        if (method == null)
        {
            Plugin.Logger?.LogError(
                "DynamicEpilogues target method resolved to null."
            );
            return null;
        }

        Plugin.Logger?.LogInfo(
            $"DynamicEpilogues target method: " +
            $"{method.DeclaringType?.FullName}.{method.Name}"
        );

        return method;
    }

    private static void Postfix(
        ref IEnumerator __result,
        Chapter20Controller __instance)
    {
        if (__result == null)
        {
            Plugin.Logger?.LogError(
                "Chapter20VictoryPatch.Postfix executed with null IEnumerator result."
            );
            return;
        }

        Plugin.Logger?.LogInfo(
            "Chapter20VictoryPatch.Postfix executing; wrapping Victory IEnumerator."
        );

        __result =
            DynamicEpilogueRuntime.WrapVictory(
                __result,
                __instance
            );
    }
}

[HarmonyPatch(typeof(StandardUISubtitleControls), nameof(StandardUISubtitleControls.ShowSubtitle))]
internal static class StandardUISubtitleControlsShowSubtitlePatch
{
    private static void Postfix(
        Subtitle subtitle)
    {
        DynamicEpilogueRuntime.HandleDynamicEpilogueSubtitle(
            subtitle
        );
    }
}
