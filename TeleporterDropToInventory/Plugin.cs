using BepInEx;
using System;
using System.Security.Permissions;
using System.Security;
using RoR2;
using System.Collections.Generic;
using BepInEx.Configuration;
using System.Text.RegularExpressions;
using UnityEngine;

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace R2API.Utils
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public class ManualNetworkRegistrationAttribute : Attribute
    {
    }
}

namespace TeleporterDropToInventory
{
    [BepInIncompatibility("shbones.MoreMountains")]
    [BepInPlugin("com.Moffein.TeleporterDropToInventory", "TeleporterDropToInventory", "1.1.0")]
    public class TeleporterDropToInventoryPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> printToChat;
        public static ConfigEntry<bool> requireAlive;
        public static ConfigEntry<bool> randomizeOrder;
        public static bool blacklistCommand;
        public static string blacklistedArtifactsString;

        public static HashSet<ArtifactDef> blacklistedArtifacts = new HashSet<ArtifactDef>();
        private void Awake()
        {
            printToChat = Config.Bind<bool>("Settings", "Print to Chat", false, "Print drops to chat.");
            requireAlive = Config.Bind<bool>("Settings", "Require Alive", false, "Only drop items to living players.");
            blacklistCommand = Config.Bind<bool>("Settings", "Blacklist Command", true, "This mod does not take effect if Command is enabled.").Value;
            blacklistedArtifactsString = Config.Bind<string>("Settings", "Blacklisted Artifacts", "", "Disable this mod when any of these artifacts are enabled. Comma separated list (ex. Sacrifice, Command, Vengeance).").Value;
            randomizeOrder = Config.Bind<bool>("Settings", "Randomize Order", true, "Randomize drop order so it doesn't always start with the host.");

            RoR2Application.onLoad += OnLoad;
            On.RoR2.BossGroup.DropRewards += BossGroup_DropRewards;
        }

        private void OnLoad()
        {
            if (blacklistCommand)
            {
                blacklistedArtifacts.Add(RoR2Content.Artifacts.Command);
            }

            if (blacklistedArtifactsString.Trim().Length > 0)
            {
                var split = blacklistedArtifactsString.Split(",");
                foreach (var str in split)
                {
                    string name = str.Trim();
                    ArtifactDef artifact = ArtifactCatalog.FindArtifactDef(name);
                    if (!artifact) artifact = GetArtifactDefFromString(name);

                    if (artifact)
                    {
                        blacklistedArtifacts.Add(artifact);
                    }
                }
            }
        }

        private static bool BlacklistedArtifactEnabled()
        {
            if (RunArtifactManager.instance)
            {
                foreach (ArtifactDef artifact in blacklistedArtifacts)
                {
                    if (RunArtifactManager.instance.IsArtifactEnabled(artifact))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void BossGroup_DropRewards(On.RoR2.BossGroup.orig_DropRewards orig, BossGroup self)
        {
            if (!BlacklistedArtifactEnabled())
            {
                DropRewardsToInventory(self);
            }
            else
            {
                orig(self);
            }
        }

        private static void DropRewardsToInventory(BossGroup self)
        {
            if (!Run.instance)
            {
                Debug.LogError("No valid run instance!");
                return;
            }
            if (self.rng == null)
            {
                Debug.LogError("RNG is null!");
                return;
            }
            if (!self.dropPosition)
            {
                Debug.LogWarning("dropPosition not set for BossGroup! No item will be spawned.");
                return;
            }

            int participatingPlayerCount = Run.instance.participatingPlayerCount;
            if (participatingPlayerCount == 0) return;

            //Resolve the default pickup
            PickupIndex pickupIndex = PickupIndex.none;
            if (self.dropTable)
            {
                pickupIndex = self.dropTable.GenerateDrop(self.rng);
            }
            else
            {
                List<PickupIndex> list = Run.instance.availableTier2DropList;
                if (self.forceTier3Reward)
                {
                    list = Run.instance.availableTier3DropList;
                }
                pickupIndex = self.rng.NextElementUniform<PickupIndex>(list);
            }
            ItemIndex item = ItemIndex.None;
            PickupDef defaultPickup = PickupCatalog.GetPickupDef(pickupIndex);
            if (defaultPickup != null && defaultPickup.itemIndex != ItemIndex.None)
            {
                item = defaultPickup.itemIndex;
            }

            //Get total drop count
            int dropCount = 1 + self.bonusRewardCount;
            if (self.scaleRewardsByPlayerCount)
            {
                dropCount *= participatingPlayerCount;
            }

            //Roll boss items first
            Queue<ItemIndex> bossItems = new Queue<ItemIndex>();
            bool hasBossDrops = self.bossDrops != null && self.bossDrops.Count > 0;
            bool hasBossDropTable = self.bossDropTables != null && self.bossDropTables.Count > 0;
            if (self.bossDrops != null && ((hasBossDrops || hasBossDropTable)))
            {
                for (int i = 0; i < dropCount; i++)
                {
                    PickupIndex index = PickupIndex.none;
                    //Roll for boss item on each item
                    if (self.rng.nextNormalizedFloat <= self.bossDropChance)
                    {
                        if (hasBossDropTable)
                        {
                            PickupDropTable pickupDropTable = self.rng.NextElementUniform<PickupDropTable>(self.bossDropTables);
                            if (pickupDropTable != null)
                            {
                                index = pickupDropTable.GenerateDrop(self.rng);
                            }
                        }
                        else
                        {
                            index = self.rng.NextElementUniform<PickupIndex>(self.bossDrops);
                        }
                    }

                    //Store rolled boss items
                    if (index != PickupIndex.none)
                    {
                        PickupDef pd = PickupCatalog.GetPickupDef(index);
                        if (pd != null && pd.itemIndex != ItemIndex.None)
                        {
                            bossItems.Enqueue(pd.itemIndex);
                        }
                    }
                }
            }

            //Distribute items
            int itemsGranted = 0;

            int playerCount = PlayerCharacterMasterController.instances.Count;
            int firstDropIndex = 0;
            int pIndex = 0;
            if (randomizeOrder.Value)
            {
                firstDropIndex = UnityEngine.Random.Range(0, playerCount);
            }

            while (itemsGranted < dropCount)
            {
                int initialItemsGranted = itemsGranted;
                foreach (var player in PlayerCharacterMasterController.instances)
                {
                    if (pIndex < firstDropIndex)
                    {
                        pIndex++;
                        continue;
                    }

                    if (player.master && player.master.inventory && (!requireAlive.Value || !player.master.IsDeadAndOutOfLivesServer()))
                    {
                        PickupDef toDrop = defaultPickup;
                        ItemIndex toGive = item;
                        if (bossItems.Count > 0)
                        {
                            toGive = bossItems.Dequeue();
                            PickupIndex bossIndex = PickupCatalog.FindPickupIndex(toGive);
                            toDrop = PickupCatalog.GetPickupDef(bossIndex);
                        }

                        if (printToChat.Value)
                        {
                            CharacterBody body = player.master.GetBody();
                            if (body)
                            {
                                Chat.AddPickupMessage(body, ((toDrop != null) ? toDrop.nameToken : null) ?? PickupCatalog.invalidPickupToken, (toDrop != null) ? toDrop.baseColor : Color.black, 1);
                            }
                        }

                        player.master.inventory.GiveItem(toGive);
                        itemsGranted++;
                    }
                }

                //Prevent potential infinite loop
                if (itemsGranted == initialItemsGranted) break;
            }
        }

        //Using code from MidRunArtifacts since there's no easy way that I know of for users to find internal artifact names.
        //Taken from https://github.com/KingEnderBrine/-RoR2-MidRunArtifacts/
        private static ArtifactDef GetArtifactDefFromString(string partialName)
        {
            //Attempt to match internal name before doing a partial name match
            ArtifactDef match = ArtifactCatalog.FindArtifactDef(partialName);
            if (match) return match;

            foreach (var artifact in ArtifactCatalog.artifactDefs)
            {
                if (GetArgNameForAtrifact(artifact).ToLower().Contains(partialName.ToLower()))
                {
                    return artifact;
                }
            }
            return null;
        }
        //Taken from https://github.com/KingEnderBrine/-RoR2-MidRunArtifacts/
        private static string GetArgNameForAtrifact(ArtifactDef artifactDef)
        {
            return Regex.Replace(Language.GetString(artifactDef.nameToken), "[ '-]", String.Empty);
        }
    }
}
