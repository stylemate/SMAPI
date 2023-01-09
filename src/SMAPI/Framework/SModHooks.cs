using System;
using System.Threading.Tasks;
using StardewModdingAPI.Enums;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace StardewModdingAPI.Framework
{
    /// <summary>Invokes callbacks for mod hooks provided by the game.</summary>
    internal class SModHooks : DelegatingModHooks
    {
        /*********
        ** Fields
        *********/
        /// <summary>A callback to invoke before <see cref="Game1.newDayAfterFade"/> runs.</summary>
        private readonly Action BeforeNewDayAfterFade;

        /// <summary>Writes messages to the console.</summary>
        private readonly IMonitor Monitor;

        /// <summary>A callback to invoke when the load stage changes.</summary>
        private readonly Action<LoadStage> OnStageChanged;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="parent">The underlying hooks to call by default.</param>
        /// <param name="beforeNewDayAfterFade">A callback to invoke before <see cref="Game1.newDayAfterFade"/> runs.</param>
        /// <param name="onStageChanged">A callback to invoke when the load stage changes.</param>
        /// <param name="monitor">Writes messages to the console.</param>
        public SModHooks(ModHooks parent, Action beforeNewDayAfterFade, Action<LoadStage> onStageChanged, IMonitor monitor)
            : base(parent)
        {
            this.BeforeNewDayAfterFade = beforeNewDayAfterFade;
            this.OnStageChanged = onStageChanged;
            this.Monitor = monitor;
        }

        /// <inheritdoc />
        public override void OnGame1_NewDayAfterFade(Action action)
        {
            this.BeforeNewDayAfterFade();
            action();
        }

        /// <inheritdoc />
        public override Task StartTask(Task task, string id)
        {
            this.Monitor.Log($"Synchronizing '{id}' task...");
            task.RunSynchronously();
            this.Monitor.Log("   task complete.");
            return task;
        }

        /// <inheritdoc />
        public override Task<T> StartTask<T>(Task<T> task, string id)
        {
            this.Monitor.Log($"Synchronizing '{id}' task...");
            task.RunSynchronously();
            this.Monitor.Log("   task complete.");
            return task;
        }

        /// <inheritdoc />
        public override void CreatedInitialLocations()
        {
            this.OnStageChanged(LoadStage.CreatedInitialLocations);
        }

        /// <inheritdoc />
        public override void SaveAddedLocations()
        {
            this.OnStageChanged(LoadStage.SaveAddedLocations);
        }
    }
}
