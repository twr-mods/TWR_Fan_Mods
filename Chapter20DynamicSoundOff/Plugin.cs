using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using PixelCrushers.DialogueSystem;
using UnityEngine;

namespace Chapter20DynamicSoundOff;

[BepInPlugin(
    MyPluginInfo.PLUGIN_GUID,
    MyPluginInfo.PLUGIN_NAME,
    MyPluginInfo.PLUGIN_VERSION
)]
public sealed class Plugin : BaseUnityPlugin
{
    private static readonly Regex LoadingSlotRegex =
        new Regex(
            @"^Loading Slot\s+(\d+)\.\.\.$",
            RegexOptions.Compiled
        );

    private int? _activeSaveSlot;

    private const string RevolutionTargetConversation =
        "Chapter20R/Start/SpeechSlyker";

    private const string ReformTargetConversation =
        "Chapter20A/Start/RangersAssemble";

    // Chapter 20's original conversation uses RorickA as the conversant.
    private const int RorickAActorId = 76;

    internal static Plugin Instance { get; private set; }
    internal static ManualLogSource Log { get; private set; }

    private Harmony _harmony;
    private ConfigEntry<string> _saveFileOverride;
    private int _lastPreparedFrame = -1;
    private string _lastPreparedTitle = string.Empty;

    private static readonly SpeechBlock[] Intro =
    {
        new(
            "Slyker",
            18,
            "ALRIGHT, RANGERS!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "TODAY WE FINALLY CUT OUT THE ROT THAT'S BEEN EATING OUR BEAUTIFUL FERN FROM THE INSIDE!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "See that guy over there in the fancy armor?",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "He's the reason we haven't had a proper night's sleep for weeks.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "The one who's been killing our comrades and loved ones for months.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "The very embodiment of greed...",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "The man who's condemned our families to years of hunger and death!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Today, we show him what happens when he messes with us and our people!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "TO ME, RANGERS!!!",
            requiresLivingCharacter: false
        )
    };

    private static readonly CharacterSpeech[] CharacterSpeeches =
    {
        new(
            "Arland",
            22,
            "Stay behind me, friends.",
            "As long as I stand, none of you will fall tonight."
        ),
        new(
            "Abigail",
            36,
            "I've spent my life watchin' men like him prey on the good folk around me.",
            "Today, I'm puttin' an arrow through the heart of it."
        ),
        new(
            "Phoebe",
            37,
            "I ran away to find a life worth living.",
            "Now I know what I'm fighting for."
        ),
        new(
            "Reyson",
            40,
            "I've walked in the shadows since before most of you held a blade.",
            "Stay out of my way... and I'll carve us a path through."
        ),
        new(
            "Elias",
            41,
            "Watch me, Father... I will finally end what you couldn't..."
        ),
        new(
            "Cedric",
            52,
            "Oh Lord, please forgive us for what we must do...",
            "Let this be the last battle we will have to face on this blood-stained path..."
        ),
        new(
            "Kaelith",
            53,
            "We've survived hunger, chains, and every bastard who thought they owned us.",
            "Let's show them what REAL hell tastes like, boys."
        ),
        new(
            "Cassidy",
            83,
            "..."
        ),
        new(
            "Benjen",
            34,
            "General Wesley entrusted this line to me. It will not break."
        ),
        new(
            "Rho",
            33,
            "Anyone who wants past me will have to earn it."
        ),
        new(
            "Sven",
            38,
            "Hah! The gods are smiling upon us tonight, benefactor!",
            "Keep my sister safe... and leave the fighting to me!"
        ),
        new(
            "Finn",
            59,
            "Dad... I'm finally moving forward.",
            "Watch me."
        ),
        new(
            "Melanie",
            62,
            "Funny how one dusty book brought me all the way here...",
            "Might as well see it through this time, right Kaelith?"
        ),
        new(
            "Ajax",
            58,
            "Hahaha! Knew you lot were fun the moment we met!",
            "Now let's go shake the whole damn world!"
        ),
        new(
            "Talon",
            60,
            "One last song...",
            "Let's make sure it's one they'll never forget."
        ),
        new(
            "Edward",
            65,
            "Heh... if they couldn't catch me before... What makes them think they can now?",
            "Try to keep up!"
        ),
        new(
            "Hilda",
            39,
            "The Valkyries have not called for any of you yet.",
            "Stay close, and I'll make sure they keep waiting...",
            "Just look after my brother."
        ),
        new(
            "Quincy",
            66,
            "Let them hit me.",
            "I've always been at my best when things get ugly."
        ),
        new(
            "Douglas",
            67,
            "Lady Phoebe, stay behind me.",
            "Every wound they give me is one more reason they'll never reach you."
        ),
        new(
            "Jack",
            91,
            "One last drink before the end of the world... Glug... glug... glug...",
            "Hah! Now I'm ready—point me at the bastards!"
        ),
        new(
            "Shiva",
            68,
            "My father believes power is taken through fear...",
            "From the shadows, I'll show him what trust can accomplish."
        ),
        new(
            "Corvin",
            69,
            "Poison for them, antidote for you—do try not to mix them up.",
            "And should that fail... Everburn taught me the value of decisive results."
        ),
        new(
            "Wesley",
            27,
            "Remember your training and watch the soldier beside you.",
            "We end this war together."
        ),
        new(
            "Sylvia",
            92,
            "Wesley stays breathing, I get paid. Simple.",
            "Keep their eyes on you... I'll handle the ones who think they're safe."
        ),
        new(
            "Salvatore",
            70,
            "You burned my fortune, bruised my pride...",
            "And gave me the time of my life, hahaha!",
            "Come now, Rangers—point me toward the nearest enemy!"
        ),
        new(
            "Jorah",
            72,
            "Been in this business longer than most of them have been alive.",
            "Your Highness, I'll get us through—same as always."
        ),
        // NoahAdvanced is the late-game portrait actor in the database.
        // The save key remains Noah0CurrentVitality.
        new(
            "Noah",
            93,
            "Twice I ran. Twice you spared me.",
            "No more running—send me their finest.",
            "I'll carve a path to freedom through them."
        ),
        new(
            "Illyana",
            12,
            "Mom... Dad... Please give me the strength to see this through.",
            "Our people are this close to finally gaining their freedom...",
            "All that suffering... All the pain and humiliation we've been through...",
            "I will cut through ALL of it tonight..."
        )
    };

    private static readonly SpeechBlock[] Outro =
    {
        new(
            "Slyker",
            18,
            "So this is it, huh... The path I've chosen for us...",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "It might become Fern's long-desired salvation... or its certain doom...",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "But no matter where it leads us...",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "We WILL move FORWARD...",
            requiresLivingCharacter: false
        )
    };

    private static readonly SpeechBlock[] Intro_reform =
    {
        new(
            "Slyker",
            18,
            "ALRIGHT, RANGERS! LISTEN UP!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "I know what they've done. I know what Fern has taken from us... from all of us.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "And I know damn well a few promises from men in fancy armor won't make any of it right.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "But look around you!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "How many more fathers? How many more sisters? How many more friends do we have to bury",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "before we finally admit that killing one another isn't fixing a damn thing?!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Crawford will answer for his part in this. Fern will change.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "But not like this.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Not over another mountain of our own people!",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Today, we stop this war.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "TO ME, RANGERS!",
            requiresLivingCharacter: false
        )
    };

    private static readonly CharacterSpeech[] CharacterSpeeches_reform =
    {
        new(
            "Arland",
            22,
            "I swore my shield would protect those who could not protect themselves.",
            "That includes protecting them from a war that has already taken too much."
        ),
        new(
            "Abigail",
            36,
            "I know what empty bellies look like. I know what happens when powerful folk forget the people beneath 'em.",
            "But dead men don't eat... And graves don't grow crops.",
            "I'm stoppin' this here."
        ),
        new(
            "Phoebe",
            37,
            "Running away taught me what privilege allowed me to ignore.",
            "I will not run from that truth again.",
            "If my name can force Fern to change without taking another thousand lives...",
            "Then for once, I will use it."
        ),
        new(
            "Reyson",
            40,
            "Seen plenty of men convince themselves one more corpse would fix everything.",
            "Never does.",
            "Let's finish this before the shadows get any more crowded."
        ),
        new(
            "Elias",
            41,
            "My father taught me that duty meant following the path laid before me.",
            "He was wrong.",
            "Sometimes duty means being the one who says... Enough."
        ),
        new(
            "Cedric",
            52,
            "Lord... we have buried enough children of Fern.",
            "Rebel, soldier, noble, commoner...",
            "Your earth receives them all the same.",
            "Please... Let this be the battle where we finally remember that."
        ),
        new(
            "Kaelith",
            53,
            "Don't mistake me for one of Crawford's lapdogs.",
            "I know exactly what his kind did to people like us.",
            "But I've spent my whole life keeping hungry people alive...",
            "I'm not about to watch them become kindling for somebody else's perfect future.",
            "Not even yours, Illyana."
        ),
        new(
            "Cassidy",
            83,
            "..."
        ),
        new(
            "Benjen",
            34,
            "General Wesley taught us what the uniform is supposed to protect.",
            "Not a throne. Not pride.",
            "The people... I intend to remember that today."
        ),
        new(
            "Rho",
            33,
            "I've held lines against worse than this. Difference is...",
            "This time, I'm holding it so nobody has to cross it."
        ),
        new(
            "Sven",
            38,
            "There was a time I would have called vengeance justice.",
            "Hah... Perhaps Hilda finally beat some sense into me.",
            "The dead have had enough company.",
            "Today, we fight for those still breathing!"
        ),
        new(
            "Finn",
            59,
            "I thought revenge would make losing Dad hurt less.",
            "It didn't. It just gave the hurt somewhere else to go.",
            "I'm not letting Fern learn that lesson the same way I did."
        ),
        new(
            "Melanie",
            62,
            "I stole one stupid book and somehow ended up in the middle of a civil war...",
            "Again. If we're really changing the world this time...",
            "Can we try doing it without burning the whole thing down?"
        ),
        new(
            "Ajax",
            58,
            "Hah...",
            "Never thought I'd be the one telling people there's such a thing as TOO much fighting.",
            "But here we are! Come on, Rangers!",
            "Let's knock some sense into 'em before there's nobody left to celebrate with!"
        ),
        new(
            "Talon",
            60,
            "Every battle gives me another name to put into a song.",
            "I used to think that was noble.",
            "Now... I'd rather have fewer verses."
        ),
        new(
            "Edward",
            65,
            "You know, I usually prefer escaping impossible situations.",
            "Unfortunately, everyone I like seems determined to stand right in the middle of this one.",
            "Fine. Let's end it before someone damages my handsome face."
        ),
        new(
            "Hilda",
            39,
            "The Valkyries have already carried too many from this field.",
            "They will receive no more without an argument from me.",
            "Sven... For once, try to help me keep people alive instead of impressing the gods."
        ),
        new(
            "Quincy",
            66,
            "I can take another hit.",
            "And another. And another... But Fern can't...",
            "So if somebody has to stand in the middle of this mess until everyone calms down...",
            "Hah. Guess I'm qualified."
        ),
        new(
            "Douglas",
            67,
            "Lady Phoebe chose to stay and change this country rather than abandon it.",
            "I chose to protect that decision.",
            "Anyone who would turn Fern's future into another battlefield...",
            "Will go through me first."
        ),
        new(
            "Jack",
            91,
            "One last drink before we save the kingdom from itself...",
            "Glug... glug... glug...",
            "Ahh! Right.",
            "Now point me toward whoever still thinks killing each other is a good idea."
        ),
        new(
            "Shiva",
            68,
            "My father taught me that fear ends arguments quickly.",
            "He neglected to mention that it creates ten more afterward.",
            "Fern has spilled enough blood proving him right.",
            "Let us try something more difficult."
        ),
        new(
            "Corvin",
            69,
            "Fascinating...",
            "Fern appears determined to treat every political illness with amputation.",
            "A remarkably effective procedure...",
            "provided one does not mind losing the patient.",
            "I suggest we attempt treatment instead."
        ),
        new(
            "Wesley",
            27,
            "Soldiers!",
            "Look carefully at the people standing across from you.",
            "They are not monsters. They are citizens of the same country we swore to protect.",
            "Disarm them if you can. Defeat them if you must.",
            "But remember why we stand here:",
            "Fern needs reform. Not another generation of graves."
        ),
        new(
            "Sylvia",
            92,
            "Wesley wants everyone brought home alive.",
            "Naturally, he chose the most inconvenient possible objective.",
            "Keep them busy. I'll handle anyone who forgets we're trying to STOP a massacre."
        ),
        new(
            "Salvatore",
            70,
            "My friends, revolution is terrible for business.",
            "Burned roads, dead customers, ruined estates...",
            "Ghastly. And before anyone accuses me of cowardice...",
            "A dead country has no future to profit from.",
            "Today, I find myself unusually invested in peace!"
        ),
        new(
            "Jorah",
            72,
            "Seen kingdoms change kings. Seen rebels become governors. Seen governors become tyrants.",
            "Funny thing is, the graves always look the same afterward.",
            "I'm too old to watch another generation make the same mistake."
        ),
        // NoahAdvanced is the late-game portrait actor in the database.
        // The save key remains Noah0CurrentVitality.
        new(
            "Noah",
            93,
            "I spent years running whenever things became difficult.",
            "Then you people kept giving me another chance.",
            "So no... I'm not running today.",
            "But I'm not carving freedom through a pile of Fern's dead either.",
            "This time... I choose where I stand."
        ),
        new(
            "Illyana",
            12,
            "You really believe them...? After everything we've seen?",
            "After every empty promise... every chain...",
            "every family left starving while men like Crawford sat comfortably behind their walls?!",
            "How many years should our people wait this time, Slyker?",
            "Five?",
            "Ten?",
            "Another generation?!",
            "No...",
            "I'm done asking them to give us what should have been ours from birth.",
            "If freedom has to be taken...",
            "Then I will take it."
        )
    };

    private static readonly SpeechBlock[] Outro_reform =
    {
        new(
            "Slyker",
            18,
            "Illy...",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "I'm not asking you to forgive them.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Hell, I don't know if I ever will.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "And maybe you're right.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Maybe tomorrow they'll lie to us.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Maybe the nobles will drag their feet. Maybe Fern will fight us every damn step of the way.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "But if that happens...",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Then we fight THAT battle tomorrow.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "We expose them. We force them.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "We drag this country forward kicking and screaming if we have to.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "But I won't keep burying our friends just because blood is faster than change.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "I can't.",
            requiresLivingCharacter: false
        ),
        new(
            "Slyker",
            18,
            "Not anymore.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "Illyana...",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "You once told me freedom had to be strong because soft hope breaks too easily.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "Maybe you were right.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "But this...",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "This isn't hope anymore.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "Look around us.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "Fern's children are killing Fern's children",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "while the people we wanted to save hide in their homes and pray we never reach them.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "I know Crawford doesn't deserve our trust.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "Maybe none of them do.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "But trust isn't what I'm giving them.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "I'm giving them one chance.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "One chance to prove that this country can change without us tearing it apart first.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "And if they waste it...",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "I swear I'll stand beside you and demand better.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "But I will not kill another friend today just to make tomorrow arrive faster.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "...Please, Illy.",
            requiresLivingCharacter: false
        ),
        new(
            "Marcus",
            19,
            "Put down your sword.",
            requiresLivingCharacter: false
        )
    };

    private void Awake()
    {
        Instance = this;
        Log = base.Logger;

        _saveFileOverride = Config.Bind(
            "Save File",
            "PathOverride",
            string.Empty,
            "Optional full path to a Those Who Rule .es3 save. " +
            "Leave blank to automatically use the slot selected in-game."
        );

        Application.logMessageReceived += HandleUnityLog;

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll();

        Log.LogInfo(
            $"Plugin {MyPluginInfo.PLUGIN_GUID} loaded. " +
            $"Waiting for '{RevolutionTargetConversation}' or " +
            $"'{ReformTargetConversation}'."
        );
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= HandleUnityLog;

        _harmony?.UnpatchSelf();
    }

    private void HandleUnityLog(
        string message,
        string stackTrace,
        LogType logType)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Match match =
            LoadingSlotRegex.Match(message.Trim());

        if (!match.Success)
        {
            return;
        }

        if (!int.TryParse(
                match.Groups[1].Value,
                out int slotNumber))
        {
            Logger.LogWarning(
                $"Saw a save-slot loading message but could not parse it: {message}"
            );

            return;
        }

        _activeSaveSlot = slotNumber;

        Logger.LogInfo(
            $"Detected active save slot: {slotNumber}"
        );
    }

    internal void PrepareConversation(string title)
    {
        if (!IsTargetConversation(title))
        {
            return;
        }

        // One game-facing overload may call another overload in the same frame.
        // Avoid rebuilding the conversation twice for that call chain.
        if (_lastPreparedFrame == Time.frameCount &&
            string.Equals(
                _lastPreparedTitle,
                title,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastPreparedFrame = Time.frameCount;
        _lastPreparedTitle = title;

        try
        {
            var sidedWithAugust =
                Utilities.GetDialogueDBKeyValue(
                    DialogueDBConstants.SIDED_WITH_AUGUST
                );

            bool isRevolutionPath =
                sidedWithAugust == 0;

            string targetConversation =
                isRevolutionPath
                    ? RevolutionTargetConversation
                    : ReformTargetConversation;

            if (!string.Equals(
                    title,
                    targetConversation,
                    StringComparison.Ordinal))
            {
                Log.LogInfo(
                    $"Skipped '{title}' because SIDED_WITH_AUGUST=" +
                    $"{sidedWithAugust}; selected target is " +
                    $"'{targetConversation}'."
                );
                return;
            }

            SpeechBlock[] intro =
                isRevolutionPath
                    ? Intro
                    : Intro_reform;

            CharacterSpeech[] characterSpeeches =
                isRevolutionPath
                    ? CharacterSpeeches
                    : CharacterSpeeches_reform;

            SpeechBlock[] outro =
                isRevolutionPath
                    ? Outro
                    : Outro_reform;

            Log.LogInfo(
                isRevolutionPath
                    ? "Chapter 20 revolution path detected; rewriting " +
                      $"{RevolutionTargetConversation}."
                    : "Chapter 20 reform path detected; rewriting " +
                      $"{ReformTargetConversation}."
            );

            DialogueDatabase database =
                DialogueManager.MasterDatabase;

            if (database == null)
            {
                Log.LogError(
                    $"Cannot rewrite '{title}': " +
                    "DialogueManager.MasterDatabase is null."
                );
                return;
            }

            string savePath = ResolveSaveFilePath();

            if (string.IsNullOrWhiteSpace(savePath))
            {
                Log.LogError(
                    $"Cannot rewrite '{title}': no save file was found. " +
                    "The original conversation will be left unchanged."
                );
                return;
            }

            SaveSnapshot save = SaveSnapshot.Load(savePath);
            RewriteConversation(
                database,
                save,
                targetConversation,
                intro,
                characterSpeeches,
                outro
            );
        }
        catch (Exception exception)
        {
            Log.LogError(
                $"Failed to prepare '{title}'. " +
                "The game may continue with the original conversation. " +
                exception
            );
        }
    }

    private void RewriteConversation(
        DialogueDatabase database,
        SaveSnapshot save,
        string targetConversation,
        SpeechBlock[] intro,
        CharacterSpeech[] characterSpeeches,
        SpeechBlock[] outro)
    {
        Conversation conversation =
            database.GetConversation(targetConversation);

        if (conversation == null)
        {
            Log.LogError(
                $"Conversation not found: {targetConversation}"
            );
            return;
        }

        DialogueEntry originalRoot =
            conversation.dialogueEntries
                .FirstOrDefault(entry => entry.id == 0);

        int rootActorId =
            originalRoot?.ActorID ?? RorickAActorId;

        int rootConversantId =
            originalRoot?.ConversantID ?? 78;

        Template template = Template.FromDefault();

        DialogueEntry root = template.CreateDialogueEntry(
            0,
            conversation.id,
            "START"
        );

        root.isRoot = true;
        root.ActorID = rootActorId;
        root.ConversantID = rootConversantId;
        root.DialogueText = string.Empty;
        root.outgoingLinks.Clear();

        var replacementEntries =
            new List<DialogueEntry> { root };

        DialogueEntry previous = root;
        int nextEntryId = 1;
        var includedCharacterNames = new HashSet<string>(
            StringComparer.Ordinal
        );

        foreach (SpeechBlock block in BuildSpeechSequence(
                     save,
                     intro,
                     characterSpeeches,
                     outro))
        {
            DialogueEntry entry =
                template.CreateDialogueEntry(
                    nextEntryId,
                    conversation.id,
                    block.SaveName
                );

            entry.ActorID = block.ActorId;
            entry.ConversantID = RorickAActorId;
            entry.DialogueText =
                "[panel=0]" + block.Text;
            entry.outgoingLinks.Clear();

            previous.outgoingLinks.Add(
                new Link(
                    conversation.id,
                    previous.id,
                    conversation.id,
                    entry.id
                )
            );

            replacementEntries.Add(entry);
            previous = entry;
            nextEntryId++;

            if (block.RequiresLivingCharacter)
            {
                includedCharacterNames.Add(block.SaveName);
            }
        }

        // Replace the complete graph only after the new graph has been built.
        conversation.dialogueEntries.Clear();
        conversation.dialogueEntries.AddRange(
            replacementEntries
        );

        Log.LogInfo(
            $"Rebuilt '{targetConversation}' from save " +
            $"'{Path.GetFileName(save.Path)}': " +
            $"{includedCharacterNames.Count} living character sound-offs, " +
            $"{replacementEntries.Count - 1} spoken entries total."
        );
    }

    private IEnumerable<SpeechBlock> BuildSpeechSequence(
        SaveSnapshot save,
        SpeechBlock[] intro,
        CharacterSpeech[] characterSpeeches,
        SpeechBlock[] outro)
    {
        foreach (SpeechBlock block in intro)
        {
            yield return block;
        }

        foreach (CharacterSpeech speech in characterSpeeches)
        {
            if (save.IsCharacterAlive(
                    speech.SaveName,
                    out int? vitality))
            {
                Log.LogInfo(
                    $"Including {speech.SaveName}: " +
                    $"CurrentVitality={vitality}, " +
                    $"blocks={speech.Blocks.Length}."
                );

                foreach (SpeechBlock block in speech.Blocks)
                {
                    yield return block;
                }
            }
            else
            {
                string reason = vitality.HasValue
                    ? $"CurrentVitality={vitality.Value}"
                    : "CurrentVitality key absent";

                Log.LogInfo(
                    $"Skipping {speech.SaveName}: {reason}."
                );
            }
        }

        foreach (SpeechBlock block in outro)
        {
            yield return block;
        }
    }

    private static bool IsTargetConversation(string title)
    {
        return string.Equals(
                   title,
                   RevolutionTargetConversation,
                   StringComparison.Ordinal) ||
               string.Equals(
                   title,
                   ReformTargetConversation,
                   StringComparison.Ordinal);
    }

    private string ResolveSaveFilePath()
    {
        string configured =
            _saveFileOverride.Value?.Trim();

        if (!string.IsNullOrEmpty(configured))
        {
            string configuredPath =
                Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(
                        Paths.GameRootPath,
                        configured
                    );

            if (File.Exists(configuredPath))
            {
                Logger.LogInfo(
                    $"Using configured save file: {configuredPath}"
                );

                return configuredPath;
            }

            Logger.LogError(
                $"Configured save file does not exist: {configuredPath}"
            );

            return null;
        }

        if (!_activeSaveSlot.HasValue)
        {
            Logger.LogError(
                "The active save slot was not detected. " +
                "Expected a Unity log message such as 'Loading Slot 2...'."
            );

            return null;
        }

        string expectedFileName =
            $"thoseWhoRuleSaveFinalV1_{_activeSaveSlot.Value}.es3";

        Logger.LogInfo(
            $"Looking for active save file: {expectedFileName}"
        );

        string[] searchRoots =
        {
            Application.persistentDataPath,
            Paths.GameRootPath
        };

        foreach (string root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root) ||
                !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                string directPath =
                    Path.Combine(root, expectedFileName);

                if (File.Exists(directPath))
                {
                    Logger.LogInfo(
                        $"Using active slot {_activeSaveSlot.Value} save file: " +
                        directPath
                    );

                    return directPath;
                }

                string foundPath =
                    Directory.GetFiles(
                            root,
                            expectedFileName,
                            SearchOption.AllDirectories
                        )
                        .FirstOrDefault();

                if (!string.IsNullOrEmpty(foundPath))
                {
                    Logger.LogInfo(
                        $"Using active slot {_activeSaveSlot.Value} save file: " +
                        foundPath
                    );

                    return foundPath;
                }
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    $"Could not search '{root}' for '{expectedFileName}': " +
                    exception.Message
                );
            }
        }

        Logger.LogError(
            $"Could not find the selected slot's save file: {expectedFileName}"
        );

        return null;
    }

    private sealed class CharacterSpeech
    {
        public string SaveName { get; }
        public SpeechBlock[] Blocks { get; }

        public CharacterSpeech(
            string saveName,
            int actorId,
            params string[] blocks)
        {
            SaveName = saveName;
            Blocks = blocks
                .Select(text => new SpeechBlock(
                    saveName,
                    actorId,
                    text
                ))
                .ToArray();
        }
    }

    private sealed class SpeechBlock
    {
        public string SaveName { get; }
        public int ActorId { get; }
        public string Text { get; }
        public bool RequiresLivingCharacter { get; }

        public SpeechBlock(
            string saveName,
            int actorId,
            string text,
            bool requiresLivingCharacter = true)
        {
            SaveName = saveName;
            ActorId = actorId;
            Text = text;
            RequiresLivingCharacter =
                requiresLivingCharacter;
        }
    }

    private sealed class SaveSnapshot
    {
        public string Path { get; }
        private string Contents { get; }

        private SaveSnapshot(
            string path,
            string contents)
        {
            Path = path;
            Contents = contents;
        }

        public static SaveSnapshot Load(string path)
        {
            return new SaveSnapshot(
                path,
                File.ReadAllText(path)
            );
        }

        public bool IsCharacterAlive(
            string characterName,
            out int? currentVitality)
        {
            string key =
                characterName +
                "0CurrentVitality";

            string pattern =
                "\"" +
                Regex.Escape(key) +
                "\"\\s*:\\s*\\{" +
                "[^{}]*?" +
                "\"value\"\\s*:\\s*" +
                "(-?\\d+)";

            Match match = Regex.Match(
                Contents,
                pattern,
                RegexOptions.CultureInvariant |
                RegexOptions.Singleline
            );

            if (!match.Success)
            {
                currentVitality = null;
                return false;
            }

            if (!int.TryParse(
                    match.Groups[1].Value,
                    out int value))
            {
                currentVitality = null;
                return false;
            }

            currentVitality = value;
            return value > 0;
        }
    }
}

[HarmonyPatch]
internal static class ChapterControllerStartConversationPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(ChapterController)
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
                       parameters[0].ParameterType ==
                       typeof(string);
            });
    }

    private static void Prefix(object[] __args)
    {
        if (__args == null ||
            __args.Length == 0 ||
            __args[0] is not string title)
        {
            return;
        }

        Plugin.Instance?.PrepareConversation(title);
    }
}
