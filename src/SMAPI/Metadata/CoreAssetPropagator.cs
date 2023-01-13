using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.ContentManagers;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Internal;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.GameData.FruitTrees;
using StardewValley.GameData.LocationContexts;
using StardewValley.GameData.Tools;
using StardewValley.GameData.Weapons;
using StardewValley.GameData.WildTrees;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.WorldMaps;
using xTile;

namespace StardewModdingAPI.Metadata
{
    /// <summary>Propagates changes to core assets to the game state.</summary>
    internal class CoreAssetPropagator
    {
        /*********
        ** Fields
        *********/
        /// <summary>The main content manager through which to reload assets.</summary>
        private readonly LocalizedContentManager MainContentManager;

        /// <summary>An internal content manager used only for asset propagation. See remarks on <see cref="GameContentManagerForAssetPropagation"/>.</summary>
        private readonly GameContentManagerForAssetPropagation DisposableContentManager;

        /// <summary>Writes messages to the console.</summary>
        private readonly IMonitor Monitor;

        /// <summary>The multiplayer instance whose map cache to update.</summary>
        private readonly Multiplayer Multiplayer;

        /// <summary>Simplifies access to private game code.</summary>
        private readonly Reflector Reflection;

        /// <summary>Parse a raw asset name.</summary>
        private readonly Func<string, IAssetName> ParseAssetName;

        /// <summary>A cache of world data fetched for the current tick.</summary>
        private readonly TickCacheDictionary<string> WorldCache = new();


        /*********
        ** Public methods
        *********/
        /// <summary>Initialize the core asset data.</summary>
        /// <param name="mainContent">The main content manager through which to reload assets.</param>
        /// <param name="disposableContent">An internal content manager used only for asset propagation.</param>
        /// <param name="monitor">Writes messages to the console.</param>
        /// <param name="multiplayer">The multiplayer instance whose map cache to update.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        /// <param name="parseAssetName">Parse a raw asset name.</param>
        public CoreAssetPropagator(LocalizedContentManager mainContent, GameContentManagerForAssetPropagation disposableContent, IMonitor monitor, Multiplayer multiplayer, Reflector reflection, Func<string, IAssetName> parseAssetName)
        {
            this.MainContentManager = mainContent;
            this.DisposableContentManager = disposableContent;
            this.Monitor = monitor;
            this.Multiplayer = multiplayer;
            this.Reflection = reflection;
            this.ParseAssetName = parseAssetName;
        }

        /// <summary>Reload one of the game's core assets (if applicable).</summary>
        /// <param name="contentManagers">The content managers whose assets to update.</param>
        /// <param name="assets">The asset keys and types to reload.</param>
        /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
        /// <param name="propagatedAssets">A lookup of asset names to whether they've been propagated.</param>
        /// <param name="changedWarpRoutes">Whether the NPC pathfinding warp route cache was reloaded.</param>
        public void Propagate(IList<IContentManager> contentManagers, IDictionary<IAssetName, Type> assets, bool ignoreWorld, out IDictionary<IAssetName, bool> propagatedAssets, out bool changedWarpRoutes)
        {
            // get base name lookup
            propagatedAssets = assets
                .Select(asset => asset.Key.GetBaseAssetName())
                .Distinct()
                .ToDictionary(name => name, _ => false);

            // edit textures in-place
            {
                IAssetName[] textureAssets = assets
                    .Where(p => typeof(Texture2D).IsAssignableFrom(p.Value))
                    .Select(p => p.Key)
                    .ToArray();

                if (textureAssets.Any())
                {
                    var defaultLanguage = this.MainContentManager.GetCurrentLanguage();

                    foreach (IAssetName assetName in textureAssets)
                    {
                        bool changed = this.PropagateTexture(assetName, assetName.LanguageCode ?? defaultLanguage, contentManagers, ignoreWorld);
                        if (changed)
                            propagatedAssets[assetName] = true;
                    }

                    foreach (IAssetName assetName in textureAssets)
                        assets.Remove(assetName);
                }
            }

            // reload other assets
            changedWarpRoutes = false;
            foreach (var entry in assets)
            {
                bool changed = false;
                bool curChangedMapRoutes = false;
                try
                {
                    changed = this.PropagateOther(entry.Key, entry.Value, ignoreWorld, out curChangedMapRoutes);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"An error occurred while propagating asset changes. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                propagatedAssets[entry.Key] = changed;
                changedWarpRoutes = changedWarpRoutes || curChangedMapRoutes;
            }

            // reload NPC pathfinding cache if any map routes changed
            if (changedWarpRoutes)
                NPC.populateRoutesFromLocationToLocationList();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Propagate changes to a cached texture asset.</summary>
        /// <param name="assetName">The asset name to reload.</param>
        /// <param name="language">The language for which to get assets.</param>
        /// <param name="contentManagers">The content managers whose assets to update.</param>
        /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
        /// <returns>Returns whether an asset was loaded.</returns>
        [SuppressMessage("ReSharper", "StringLiteralTypo", Justification = "These deliberately match the asset names.")]
        private bool PropagateTexture(IAssetName assetName, LocalizedContentManager.LanguageCode language, IList<IContentManager> contentManagers, bool ignoreWorld)
        {
            /****
            ** Update textures in-place
            ****/
            assetName = assetName.GetBaseAssetName();
            bool changed = false;
            {
                Lazy<Texture2D> newTexture = new(() => this.DisposableContentManager.LoadLocalized<Texture2D>(assetName, language, useCache: false));

                foreach (IContentManager contentManager in contentManagers)
                {
                    if (contentManager.IsLoaded(assetName))
                    {
                        changed = true;
                        Texture2D texture = contentManager.LoadLocalized<Texture2D>(assetName, language, useCache: true);
                        texture.CopyFromTexture(newTexture.Value);
                    }
                }

                if (newTexture.IsValueCreated)
                    newTexture.Value.Dispose();
            }

            /****
            ** Update game state if needed
            ****/
            if (changed)
            {
                switch (assetName.Name.ToLower().Replace("\\", "/")) // normalized key so we can compare statically
                {
                    /****
                    ** Buildings
                    ****/
                    case "buildings/houses_paintmask": // Farm
                        if (ignoreWorld)
                        {
                            this.RemoveFromPaintMaskCache(assetName);
                            Game1.getFarm()?.ApplyHousePaint();
                        }
                        break;

                    /****
                    ** Content\Characters\Farmer
                    ****/
                    case "characters/farmer/farmer_base": // Farmer
                    case "characters/farmer/farmer_base_bald":
                    case "characters/farmer/farmer_girl_base":
                    case "characters/farmer/farmer_girl_base_bald":
                        if (ignoreWorld)
                            this.UpdatePlayerSprites(assetName);
                        break;

                    /****
                    ** Content\TileSheets
                    ****/
                    case "tilesheets/tools": // Game1.ResetToolSpriteSheet
                        Game1.ResetToolSpriteSheet();
                        break;

                    default:
                        if (!ignoreWorld)
                        {
                            if (assetName.IsDirectlyUnderPath("Buildings") && assetName.BaseName.EndsWith("_PaintMask"))
                                return this.UpdateBuildingPaintMask(assetName);
                        }

                        break;
                }
            }

            return changed;
        }

        /// <summary>Reload one of the game's core assets (if applicable).</summary>
        /// <param name="assetName">The asset name to reload.</param>
        /// <param name="type">The asset type to reload.</param>
        /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
        /// <param name="changedWarpRoutes">Whether the locations reachable by warps from this location changed as part of this propagation.</param>
        /// <returns>Returns whether an asset was loaded.</returns>
        [SuppressMessage("ReSharper", "StringLiteralTypo", Justification = "These deliberately match the asset names.")]
        private bool PropagateOther(IAssetName assetName, Type type, bool ignoreWorld, out bool changedWarpRoutes)
        {
            bool changed = false;
            var content = this.MainContentManager;
            string key = assetName.BaseName;
            changedWarpRoutes = false;

            /****
            ** Propagate map changes
            ****/
            if (type == typeof(Map))
            {
                if (!ignoreWorld)
                {
                    foreach (LocationInfo info in this.GetLocationsWithInfo())
                    {
                        GameLocation location = info.Location;

                        if (this.IsSameBaseName(assetName, location.mapPath.Value))
                        {
                            static ISet<string> GetWarpSet(GameLocation location)
                            {
                                return new HashSet<string>(
                                    location.warps.Select(p => p.TargetName)
                                );
                            }

                            var oldWarps = GetWarpSet(location);
                            this.UpdateMap(info);
                            var newWarps = GetWarpSet(location);

                            changedWarpRoutes = changedWarpRoutes || oldWarps.Count != newWarps.Count || oldWarps.Any(p => !newWarps.Contains(p));
                            changed = true;
                        }
                    }
                }

                return changed;
            }

            /****
            ** Propagate by key
            ****/
            switch (assetName.BaseName.ToLower().Replace("\\", "/")) // normalized key so we can compare statically
            {
                /****
                ** Content\Data
                ****/
                case "data/achievements": // Game1.LoadContent
                    Game1.achievements = content.Load<Dictionary<int, string>>(key);
                    return true;

                case "data/audiocuemodificationdata":
                    Game1.CueModification.OnStartup(); // reload file and reapply changes
                    return true;

                case "data/bigcraftablesinformation": // Game1.LoadContent
                    Game1.bigCraftablesInformation = content.Load<Dictionary<string, string>>(key);
                    ItemRegistry.ResetCache();
                    return true;

                case "data/boots": // BootsDataDefinition
                    ItemRegistry.ResetCache();
                    return true;

                case "data/buildings": // Game1.LoadContent
                    Game1.buildingsData = content.Load<Dictionary<string, BuildingData>>(key);
                    return true;

                case "data/clothinginformation": // Game1.LoadContent
                    Game1.clothingInformation = content.Load<Dictionary<string, string>>(key);
                    ItemRegistry.ResetCache();
                    return true;

                case "data/concessions": // MovieTheater.GetConcessions
                    MovieTheater.ClearCachedLocalizedData();
                    return true;

                case "data/concessiontastes": // MovieTheater.GetConcessionTasteForCharacter
                    MovieTheater.ClearCachedConcessionTastes();
                    return true;

                case "data/cookingrecipes": // CraftingRecipe.InitShared
                    CraftingRecipe.cookingRecipes = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/craftingrecipes": // CraftingRecipe.InitShared
                    CraftingRecipe.craftingRecipes = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/farmanimals": // FarmAnimal constructor
                    return changed | (!ignoreWorld && this.UpdateFarmAnimalData());

                case "data/furniture": // FurnitureDataDefinition
                    ItemRegistry.ResetCache();
                    return true;

                case "data/fruittrees": // Game1.LoadContent
                    Game1.fruitTreeData = content.Load<Dictionary<string, FruitTreeData>>("Data\\FruitTrees");
                    return true;

                case "data/hairdata": // Farmer.GetHairStyleMetadataFile
                    return changed | this.UpdateHairData();

                case "data/hats": // HatDataDefinition
                    ItemRegistry.ResetCache();
                    return true;

                case "data/locationcontexts": // GameLocation.LocationContext
                    this.UpdateLocationContexts();
                    return true;

                case "data/movies": // MovieTheater.GetMovieData
                case "data/moviesreactions": // MovieTheater.GetMovieReactions
                    MovieTheater.ClearCachedLocalizedData();
                    return true;

                case "data/npcdispositions": // NPC constructor
                    return changed | (!ignoreWorld && this.UpdateNpcDispositions(content, assetName));

                case "data/npcgifttastes": // Game1.LoadContent
                    Game1.NPCGiftTastes = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/objectcontexttags": // Game1.LoadContent
                    Game1.objectContextTags = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/objectinformation": // Game1.LoadContent
                    Game1.objectInformation = content.Load<Dictionary<string, string>>(key);
                    ItemRegistry.ResetCache();
                    return true;

                case "data/tools": // Game1.LoadContent
                    Game1.toolData = content.Load<Dictionary<string, ToolData>>(key);
                    ItemRegistry.ResetCache();
                    return true;

                case "data/weapons": // Game1.LoadContent
                    Game1.weaponData = Game1.content.Load<Dictionary<string, WeaponData>>(@"Data\Weapons");
                    ItemRegistry.ResetCache();
                    return true;

                case "data/wildtrees": // Tree
                    this.Reflection.GetField<Dictionary<string, WildTreeData>?>(typeof(Tree), "_WildTreeData").SetValue(null);
                    this.Reflection.GetField<Dictionary<string, string>?>(typeof(Tree), "_WildTreeSeedLookup").SetValue(null);
                    return true;

                case "data/worldmap": // WorldMapManager
                    WorldMapManager.ReloadData();
                    return true;

                /****
                ** Content\Fonts
                ****/
                case "fonts/spritefont1": // Game1.LoadContent
                    Game1.dialogueFont = content.Load<SpriteFont>(key);
                    return true;

                case "fonts/smallfont": // Game1.LoadContent
                    Game1.smallFont = content.Load<SpriteFont>(key);
                    return true;

                case "fonts/tinyfont": // Game1.LoadContent
                    Game1.tinyFont = content.Load<SpriteFont>(key);
                    return true;

                case "fonts/tinyfontborder": // Game1.LoadContent
                    Game1.tinyFontBorder = content.Load<SpriteFont>(key);
                    return true;

                /****
                ** Content\Strings
                ****/
                case "strings/stringsfromcsfiles":
                    return changed | this.UpdateStringsFromCsFiles(content);

                /****
                ** Dynamic keys
                ****/
                default:
                    if (!ignoreWorld)
                    {
                        if (assetName.IsDirectlyUnderPath("Characters/Dialogue"))
                            return changed | this.UpdateNpcDialogue(assetName);

                        if (assetName.IsDirectlyUnderPath("Characters/schedules"))
                            return changed | this.UpdateNpcSchedules(assetName);
                    }

                    return false;
            }
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Update texture methods
        ****/
        /// <summary>Update building paint mask textures.</summary>
        /// <param name="assetName">The asset name to update.</param>
        /// <returns>Returns whether any textures were updated.</returns>
        private bool UpdateBuildingPaintMask(IAssetName assetName)
        {
            // get building type
            string type = Path.GetFileName(assetName.BaseName);
            type = type[..^"_PaintMask".Length];

            // get buildings
            Building[] buildings = this.GetLocations(buildingInteriors: false)
                .SelectMany(p => p.buildings)
                .Where(p => p.buildingType.Value == type)
                .ToArray();

            // remove from paint mask cache
            bool removedFromCache = this.RemoveFromPaintMaskCache(assetName);

            // reload textures
            if (buildings.Any())
            {
                foreach (Building building in buildings)
                    building.resetTexture();

                return true;
            }

            return removedFromCache;
        }

        /// <summary>Update the sprites for matching players.</summary>
        /// <param name="assetName">The asset name to update.</param>
        private void UpdatePlayerSprites(IAssetName assetName)
        {
            Farmer[] players =
                (
                    from player in Game1.getOnlineFarmers()
                    where this.IsSameBaseName(assetName, player.getTexture())
                    select player
                )
                .ToArray();

            foreach (Farmer player in players)
            {
                FarmerRenderer.recolorOffsets?.Clear();

                player.FarmerRenderer.MarkSpriteDirty();
            }
        }

        /****
        ** Update data methods
        ****/
        /// <summary>Update location context data.</summary>
        private void UpdateLocationContexts()
        {
            //
            // cached contexts will be reloaded on demand if null
            //

            foreach (string fieldName in new[] { "_defaultContext", "_islandContext" })
                this.Reflection.GetField<LocationContextData?>(typeof(GameLocation.LocationContext), fieldName).SetValue(null);

            foreach (GameLocation location in this.GetLocations())
                location.locationContext = null;
        }

        /// <summary>Update the data for matching farm animals.</summary>
        /// <returns>Returns whether any farm animals were updated.</returns>
        /// <remarks>Derived from the <see cref="FarmAnimal"/> constructor.</remarks>
        private bool UpdateFarmAnimalData()
        {
            bool changed = false;
            foreach (FarmAnimal animal in this.GetFarmAnimals())
            {
                animal.reloadData();
                changed = true;
            }

            return changed;
        }

        /// <summary>Update hair style metadata.</summary>
        /// <returns>Returns whether any data was updated.</returns>
        /// <remarks>Derived from the <see cref="Farmer.GetHairStyleMetadataFile"/> and <see cref="Farmer.GetHairStyleMetadata"/>.</remarks>
        private bool UpdateHairData()
        {
            if (Farmer.hairStyleMetadataFile == null)
                return false;

            Farmer.hairStyleMetadataFile = null;
            Farmer.allHairStyleIndices = null;
            Farmer.hairStyleMetadata.Clear();

            return true;
        }

        /// <summary>Update the dialogue data for matching NPCs.</summary>
        /// <param name="assetName">The asset name to update.</param>
        /// <returns>Returns whether any NPCs were updated.</returns>
        private bool UpdateNpcDialogue(IAssetName assetName)
        {
            // get NPCs
            string name = Path.GetFileName(assetName.BaseName);
            NPC[] villagers = this.GetCharacters().Where(npc => npc.Name == name && npc.isVillager()).ToArray();
            if (!villagers.Any())
                return false;

            // update dialogue
            // Note that marriage dialogue isn't reloaded after reset, but it doesn't need to be
            // propagated anyway since marriage dialogue keys can't be added/removed and the field
            // doesn't store the text itself.
            foreach (NPC villager in villagers)
            {
                bool shouldSayMarriageDialogue = villager.shouldSayMarriageDialogue.Value;
                MarriageDialogueReference[] marriageDialogue = villager.currentMarriageDialogue.ToArray();

                villager.resetSeasonalDialogue(); // doesn't only affect seasonal dialogue
                villager.resetCurrentDialogue();

                villager.shouldSayMarriageDialogue.Set(shouldSayMarriageDialogue);
                villager.currentMarriageDialogue.Set(marriageDialogue);
            }

            return true;
        }

        /// <summary>Update the disposition data for matching NPCs.</summary>
        /// <param name="content">The content manager through which to reload the asset.</param>
        /// <param name="assetName">The asset name to update.</param>
        /// <returns>Returns whether any NPCs were updated.</returns>
        private bool UpdateNpcDispositions(LocalizedContentManager content, IAssetName assetName)
        {
            IDictionary<string, string> data = content.Load<Dictionary<string, string>>(assetName.BaseName);
            bool changed = false;
            foreach (NPC npc in this.GetCharacters())
            {
                if (npc.isVillager() && data.ContainsKey(npc.Name))
                {
                    npc.reloadData();
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>Update the schedules for matching NPCs.</summary>
        /// <param name="assetName">The asset name to update.</param>
        /// <returns>Returns whether any NPCs were updated.</returns>
        private bool UpdateNpcSchedules(IAssetName assetName)
        {
            // get NPCs
            string name = Path.GetFileName(assetName.BaseName);
            NPC[] villagers = this.GetCharacters().Where(npc => npc.Name == name && npc.isVillager()).ToArray();
            if (!villagers.Any())
                return false;

            // update schedule
            foreach (NPC villager in villagers)
            {
                // reload schedule
                this.Reflection.GetField<bool>(villager, "_hasLoadedMasterScheduleData").SetValue(false);
                this.Reflection.GetField<Dictionary<string, string>?>(villager, "_masterScheduleData").SetValue(null);
                villager.TryLoadSchedule();

                // switch to new schedule if needed
                if (villager.Schedule != null)
                {
                    int lastScheduleTime = villager.Schedule.Keys.Where(p => p <= Game1.timeOfDay).OrderByDescending(p => p).FirstOrDefault();
                    if (lastScheduleTime != 0)
                    {
                        villager.queuedSchedulePaths.Clear();
                        villager.lastAttemptedSchedule = 0;
                        villager.checkSchedule(lastScheduleTime);
                    }
                }
            }
            return true;
        }

        /// <summary>Update cached translations from the <c>Strings\StringsFromCSFiles</c> asset.</summary>
        /// <param name="content">The content manager through which to reload the asset.</param>
        /// <returns>Returns whether any data was updated.</returns>
        /// <remarks>Derived from the <see cref="Game1.TranslateFields"/>.</remarks>
        private bool UpdateStringsFromCsFiles(LocalizedContentManager content)
        {
            Game1.samBandName = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.2156");
            Game1.elliottBookName = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.2157");

            string[] dayNames = this.Reflection.GetField<string[]>(typeof(Game1), "_shortDayDisplayName").GetValue();
            dayNames[0] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3042");
            dayNames[1] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3043");
            dayNames[2] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3044");
            dayNames[3] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3045");
            dayNames[4] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3046");
            dayNames[5] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3047");
            dayNames[6] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3048");

            return true;
        }

        /****
        ** Update map methods
        ****/
        /// <summary>Update the map for a location.</summary>
        /// <param name="locationInfo">The location whose map to update.</param>
        private void UpdateMap(LocationInfo locationInfo)
        {
            GameLocation location = locationInfo.Location;
            Vector2? playerPos = Game1.player?.Position;

            // remove from multiplayer cache
            this.Multiplayer.cachedMultiplayerMaps.Remove(location.NameOrUniqueName);

            // reload map
            location.interiorDoors.Clear(); // prevent errors when doors try to update tiles which no longer exist
            location.reloadMap();

            // reload interior doors
            location.interiorDoors.Clear();
            location.interiorDoors.ResetSharedState(); // load doors from map properties
            location.interiorDoors.ResetLocalState(); // reapply door tiles

            // reapply map changes (after reloading doors so they apply theirs too)
            location.MakeMapModifications(force: true);

            // update for changes
            location.updateWarps();
            location.updateDoors();
            locationInfo.ParentBuilding?.updateInteriorWarps();

            // reset player position
            // The game may move the player as part of the map changes, even if they're not in that
            // location. That's not needed in this case, and it can have weird effects like players
            // warping onto the wrong tile (or even off-screen) if a patch changes the farmhouse
            // map on location change.
            if (playerPos.HasValue)
                Game1.player!.Position = playerPos.Value;
        }

        /****
        ** Helpers
        ****/
        /// <summary>Get all NPCs in the game (excluding farm animals).</summary>
        private IEnumerable<NPC> GetCharacters()
        {
            return this.WorldCache.GetOrSet(
                nameof(this.GetCharacters),
                () =>
                {
                    List<NPC> characters = new();

                    foreach (NPC character in this.GetLocations().SelectMany(p => p.characters))
                        characters.Add(character);

                    if (Game1.CurrentEvent?.actors != null)
                    {
                        foreach (NPC character in Game1.CurrentEvent.actors)
                            characters.Add(character);
                    }

                    return characters;
                }
            );
        }

        /// <summary>Get all farm animals in the game.</summary>
        private IEnumerable<FarmAnimal> GetFarmAnimals()
        {
            return this.WorldCache.GetOrSet(
                nameof(this.GetFarmAnimals),
                () =>
                {
                    List<FarmAnimal> animals = new();

                    foreach (GameLocation location in this.GetLocations())
                    {
                        if (location is Farm farm)
                        {
                            foreach (FarmAnimal animal in farm.animals.Values)
                                animals.Add(animal);
                        }
                        else if (location is AnimalHouse animalHouse)
                        {
                            foreach (FarmAnimal animal in animalHouse.animals.Values)
                                animals.Add(animal);
                        }
                    }

                    return animals;
                }
            );
        }

        /// <summary>Get all locations in the game.</summary>
        /// <param name="buildingInteriors">Whether to also get the interior locations for constructable buildings.</param>
        private IEnumerable<GameLocation> GetLocations(bool buildingInteriors = true)
        {
            return this.WorldCache.GetOrSet(
                $"{nameof(this.GetLocations)}_{buildingInteriors}",
                () => this.GetLocationsWithInfo(buildingInteriors).Select(info => info.Location).ToArray()
            );
        }

        /// <summary>Get all locations in the game.</summary>
        /// <param name="buildingInteriors">Whether to also get the interior locations for constructable buildings.</param>
        private IEnumerable<LocationInfo> GetLocationsWithInfo(bool buildingInteriors = true)
        {
            return this.WorldCache.GetOrSet(
                $"{nameof(this.GetLocationsWithInfo)}_{buildingInteriors}",
                () =>
                {
                    List<LocationInfo> locations = new();

                    // get root locations
                    foreach (GameLocation location in Game1.locations)
                        locations.Add(new LocationInfo(location, null));
                    if (SaveGame.loaded?.locations != null)
                    {
                        foreach (GameLocation location in SaveGame.loaded.locations)
                            locations.Add(new LocationInfo(location, null));
                    }

                    // get child locations
                    if (buildingInteriors)
                    {
                        foreach (GameLocation location in locations.Select(p => p.Location).ToArray())
                        {
                            foreach (Building building in location.buildings)
                            {
                                GameLocation indoors = building.indoors.Value;
                                if (indoors is not null)
                                    locations.Add(new LocationInfo(indoors, building));
                            }
                        }
                    }

                    return locations;
                });
        }

        /// <summary>Get whether two asset names are equivalent if you ignore the locale code.</summary>
        /// <param name="left">The first value to compare.</param>
        /// <param name="right">The second value to compare.</param>
        private bool IsSameBaseName(IAssetName? left, string? right)
        {
            if (left is null || right is null)
                return false;

            IAssetName? parsedB = this.ParseAssetNameOrNull(right);
            return this.IsSameBaseName(left, parsedB);
        }

        /// <summary>Get whether two asset names are equivalent if you ignore the locale code.</summary>
        /// <param name="left">The first value to compare.</param>
        /// <param name="right">The second value to compare.</param>
        private bool IsSameBaseName(IAssetName? left, IAssetName? right)
        {
            if (left is null || right is null)
                return false;

            return left.IsEquivalentTo(right.BaseName, useBaseName: true);
        }

        /// <summary>Normalize an asset key to match the cache key and assert that it's valid, but don't raise an error for null or empty values.</summary>
        /// <param name="path">The asset key to normalize.</param>
        private IAssetName? ParseAssetNameOrNull(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return this.ParseAssetName(path);
        }
        /// <summary>Remove a case-insensitive key from the paint mask cache.</summary>
        /// <param name="assetName">The paint mask asset name.</param>
        private bool RemoveFromPaintMaskCache(IAssetName assetName)
        {
            // make cache case-insensitive
            // This is needed for cache invalidation since mods may specify keys with a different capitalization
            if (!object.ReferenceEquals(BuildingPainter.paintMaskLookup.Comparer, StringComparer.OrdinalIgnoreCase))
                BuildingPainter.paintMaskLookup = new Dictionary<string, List<List<int>>>(BuildingPainter.paintMaskLookup, StringComparer.OrdinalIgnoreCase);

            // remove key from cache
            return BuildingPainter.paintMaskLookup.Remove(assetName.BaseName);
        }

        /// <summary>Metadata about a location used in asset propagation.</summary>
        /// <param name="Location">The location instance.</param>
        /// <param name="ParentBuilding">The building which contains the location, if any.</param>
        private record LocationInfo(GameLocation Location, Building? ParentBuilding);
    }
}
