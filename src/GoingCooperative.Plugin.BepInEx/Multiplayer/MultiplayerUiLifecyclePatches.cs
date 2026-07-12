using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static bool multiplayerMainMenuLifecycleObserved;
        private static bool multiplayerMainMenuActive;
        private static Button? multiplayerNativeTutorialButton;
        private static Component? multiplayerNativeTutorialLabel;
        private static bool multiplayerNativeTutorialSearchLogged;
        private static bool multiplayerTutorialActionSuppressedLogged;

        private void TryInstallMultiplayerUiLifecyclePatches(Harmony harmonyInstance)
        {
            if (!replicationConfigMultiplayerMenuEnabled)
            {
                return;
            }

            var mainMenuTypes = FindMultiplayerLifecycleTypes("MainMenuView");
            if (mainMenuTypes.Count == 0)
            {
                LogReplicationWarning("Going Cooperative multiplayer UI lifecycle target missing type=MainMenuView.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(MultiplayerMainMenuLifecyclePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var tutorialPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(MultiplayerTutorialActionPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var patched = 0;
            var tutorialPatched = 0;
            foreach (var type in mainMenuTypes)
            {
                var allMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var methods = allMethods
                    .Where(method => !method.IsAbstract
                        && !method.IsGenericMethod
                        && method.GetParameters().Length == 0
                        && (string.Equals(method.Name, "Awake", StringComparison.Ordinal)
                            || string.Equals(method.Name, "Start", StringComparison.Ordinal)
                            || string.Equals(method.Name, "OnEnable", StringComparison.Ordinal)
                            || string.Equals(method.Name, "OnDisable", StringComparison.Ordinal)
                            || string.Equals(method.Name, "Update", StringComparison.Ordinal)
                            || string.Equals(method.Name, "LateUpdate", StringComparison.Ordinal)))
                    .Distinct()
                    .ToArray();
                foreach (var method in methods)
                {
                    harmonyInstance.Patch(method, postfix: postfix);
                    patched++;
                    LogReplicationInfo("Going Cooperative multiplayer UI lifecycle patched "
                        + type.FullName + "." + method.Name);
                }

                foreach (var method in allMethods.Where(method => !method.IsAbstract
                    && !method.IsGenericMethod
                    && !method.IsSpecialName
                    && method.Name.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    harmonyInstance.Patch(method, prefix: tutorialPrefix);
                    tutorialPatched++;
                    LogReplicationInfo("Going Cooperative multiplayer UI tutorial action patched "
                        + type.FullName + "." + method.Name);
                }
            }

            LogReplicationInfo("Going Cooperative multiplayer UI lifecycle patches="
                + patched.ToString(CultureInfo.InvariantCulture)
                + " tutorialActionPatches=" + tutorialPatched.ToString(CultureInfo.InvariantCulture));

            var loadingFinished = AccessTools.Method("NSMedieval.GlobalSaveController:OnLoadingFinished");
            var loadingFinishedPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(MultiplayerLoadingFinishedPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            if (loadingFinished != null)
            {
                harmonyInstance.Patch(loadingFinished, postfix: loadingFinishedPostfix);
                LogReplicationInfo("Going Cooperative multiplayer loading-finished lifecycle patched.");
            }
            else
            {
                LogReplicationWarning("Going Cooperative multiplayer loading-finished lifecycle target missing.");
            }

            var loadVillageData = AccessTools.Method(
                AccessTools.TypeByName("NSMedieval.GlobalSaveController"),
                "LoadVillageData",
                new[] { AccessTools.TypeByName("NSMedieval.VillageSaveInfo") });
            var loadResultPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(MultiplayerNativeLoadResultPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            if (loadVillageData != null)
            {
                harmonyInstance.Patch(loadVillageData, postfix: loadResultPostfix);
                LogReplicationInfo("Going Cooperative multiplayer native-load result patched.");
            }

        }

        private static void MultiplayerLoadingFinishedPostfix(bool afterLoad)
        {
            instance?.OnMultiplayerGameLoadingFinished(afterLoad);
        }

        private static void MultiplayerNativeLoadResultPostfix(bool __result)
        {
            if (!__result) instance?.OnMultiplayerNativeLoadFailed("Going Medieval rejected LoadVillageData.");
        }


        private static List<Type> FindMultiplayerLifecycleTypes(string simpleName)
        {
            var results = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (string.Equals(type.Name, simpleName, StringComparison.Ordinal))
                    {
                        results.Add(type);
                    }
                }
            }

            return results;
        }

        private static void MultiplayerMainMenuLifecyclePostfix(MethodBase __originalMethod, object __instance)
        {
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            if (string.Equals(__originalMethod.Name, "OnEnable", StringComparison.Ordinal)
                || string.Equals(__originalMethod.Name, "Start", StringComparison.Ordinal))
            {
                multiplayerMainMenuActive = true;
            }
            else if (string.Equals(__originalMethod.Name, "OnDisable", StringComparison.Ordinal))
            {
                multiplayerMainMenuActive = false;
            }

            if (!multiplayerMainMenuLifecycleObserved)
            {
                multiplayerMainMenuLifecycleObserved = true;
                current.LogReplicationInfo("Going Cooperative multiplayer UI main-menu lifecycle observed method="
                    + __originalMethod.DeclaringType?.FullName + "." + __originalMethod.Name
                    + " screen=" + UnityEngine.Screen.width.ToString(CultureInfo.InvariantCulture)
                    + "x" + UnityEngine.Screen.height.ToString(CultureInfo.InvariantCulture));
            }

            // The BepInEx plugin component is destroyed during the main-menu scene
            // transition, so its Update method cannot pump a UI-started connection.
            // MainMenuView.Update is game-owned and reliable; the runtime frame guard
            // keeps the other lifecycle postfixes from pumping more than once per frame.
            current.UpdateReplicationRuntime();
            current.UpdateMultiplayerCanvasGuiSafely();
            current.TryRemapNativeTutorialButton(__instance);
        }

        private static bool MultiplayerTutorialActionPrefix(MethodBase __originalMethod)
        {
            if (!replicationConfigMultiplayerMenuEnabled)
            {
                return true;
            }

            if (!multiplayerTutorialActionSuppressedLogged)
            {
                multiplayerTutorialActionSuppressedLogged = true;
                instance?.LogReplicationInfo("Going Cooperative suppressed native Tutorial action method="
                    + __originalMethod.DeclaringType?.FullName + "." + __originalMethod.Name);
            }

            return false;
        }

        private void TryRemapNativeTutorialButton(object mainMenuView)
        {
            if (!replicationConfigMultiplayerMenuEnabled)
            {
                return;
            }

            if (multiplayerNativeTutorialButton != null)
            {
                SetMultiplayerNativeButtonLabel(multiplayerNativeTutorialLabel, "MULTIPLAYER");
                SetMultiplayerCanvasLauncherVisible(false);
                return;
            }

            if (!(mainMenuView is Component mainMenuComponent))
            {
                return;
            }

            var buttons = mainMenuComponent.GetComponentsInChildren<Button>(true);
            var candidates = new List<string>();
            foreach (var button in buttons)
            {
                var label = FindMultiplayerNativeButtonLabel(button, out var labelText);
                candidates.Add(button.name + "=" + labelText);
                if (button.name.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) < 0
                    && labelText.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                multiplayerNativeTutorialButton = button;
                multiplayerNativeTutorialLabel = label;
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    var current = instance;
                    if (!ReferenceEquals(current, null))
                    {
                        current.OpenMultiplayerCanvasFromNativeMenu();
                    }
                });
                SetMultiplayerNativeButtonLabel(label, "MULTIPLAYER");
                SetMultiplayerCanvasLauncherVisible(false);
                SetMultiplayerCanvasOpen(false);
                LogReplicationInfo("Going Cooperative remapped native Tutorial button name="
                    + button.name + " previousLabel=" + labelText);
                return;
            }

            if (!multiplayerNativeTutorialSearchLogged)
            {
                multiplayerNativeTutorialSearchLogged = true;
                LogReplicationWarning("Going Cooperative native Tutorial button not found candidates="
                    + string.Join(" | ", candidates.ToArray()));
            }
        }

        private static Component? FindMultiplayerNativeButtonLabel(Button button, out string text)
        {
            foreach (var component in button.GetComponentsInChildren<Component>(true))
            {
                if (component is Text unityText && !string.IsNullOrWhiteSpace(unityText.text))
                {
                    text = unityText.text.Trim();
                    return unityText;
                }

                var property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property?.PropertyType != typeof(string) || !property.CanRead)
                {
                    continue;
                }

                try
                {
                    var value = property.GetValue(component, null) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        text = value!.Trim();
                        return component;
                    }
                }
                catch
                {
                    // A third-party label component should not break menu discovery.
                }
            }

            text = string.Empty;
            return null;
        }

        private static void SetMultiplayerNativeButtonLabel(Component? label, string text)
        {
            if (label == null)
            {
                return;
            }

            if (label is Text unityText)
            {
                unityText.text = text;
                return;
            }

            var property = label.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            if (property?.PropertyType == typeof(string) && property.CanWrite)
            {
                try
                {
                    property.SetValue(label, text, null);
                }
                catch
                {
                    // The button remains functional even if a custom label rejects writes.
                }
            }
        }
    }
}
