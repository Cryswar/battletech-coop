using BattleTech;
using BattleTech.UI;
using HBS;

namespace BattletechCoop.Sync
{
    // Launches a contract from the main menu exactly the way the skirmish screen does
    // (SkirmishSettings_Beta.LaunchMap). Calling GameInstance.LaunchContract directly, without
    // Accept and the loading curtain, leaves both screens black forever (found 2026-10-16).
    public static class ContractLauncher
    {
        public static void LaunchFromMenu(Contract contract)
        {
            contract.Accept(false);
            AudioEventManager.PlayLoadingMusic();
            LoadingCurtain.ExecuteWhenVisible(() =>
            {
                var ui = LazySingletonBehavior<UIManager>.Instance;
                ui.PoolAllModules();
                ui.FlushPooledUIModules();
                UnityGameInstance.BattleTechGame.LaunchContract(contract);
            });
            LoadingCurtain.ShowUntil(() => LevelLoader.InterstitialLoaded, false, (GameTipManager.GameTipType)4, 0f);
        }
    }
}
