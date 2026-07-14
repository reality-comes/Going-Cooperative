using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private void TryInstallReplicationCombatDiagnostics(Harmony harmony)
        {
            if (!replicationConfigCombatDiagnostics) return;

            var postfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationCombatDiagnosticPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var count = 0;

            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.View.WorkerView", "StartDraft", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.View.WorkerView", "EndDraft", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.View.WorkerView", "ManualAttack", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.View.WorkerView", "CancelManualAttack", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.DraftController", "OnStartDraft", "NSMedieval.State.HumanoidInstance");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.DraftController", "OnEndDraft", "NSMedieval.State.HumanoidInstance");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.DraftController", "ExecuteDraftOrder", "NSMedieval.State.HumanoidInstance", "NSMedieval.Draft.DraftOrder");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Manager.DraftManager", "ExecuteOrder", "NSMedieval.State.HumanoidInstance", "NSMedieval.Draft.DraftOrder");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.State.WorkerBehaviour", "SetCombatMode", "NSMedieval.State.WorkerJobs.UnitCombatModeType", "System.Boolean");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Manager.CombatTargetManager", "SetPreferredTarget", "NSMedieval.Goap.IDamageDealAgent", "NSMedieval.Goap.IDamageTakingAgent");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Manager.CombatTargetManager", "RemovePreferredTarget", "NSMedieval.Goap.IDamageDealAgent");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.CombatController", "OnAttackStreamStart", "NSMedieval.Goap.IDamageDealAgent");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.CombatController", "OnAttackStreamEnd", "NSMedieval.Goap.IDamageDealAgent");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.CombatController", "OnHitMissed", "NSMedieval.Goap.IDamageDealAgent", "NSMedieval.Goap.IDamageTakingAgent", "NSMedieval.Types.CombatMissType");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.CombatController", "OnHitBlocked", "NSMedieval.Goap.IDamageDealAgent", "NSMedieval.Goap.IDamageTakingAgent", "NSMedieval.State.CombatHitInfo");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.CombatController", "OnDamageTaken", "NSMedieval.Goap.IDamageDealAgent", "NSMedieval.Goap.IDamageTakingAgent", "NSMedieval.State.CombatHitInfo");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.CombatController", "OnAgentDied", "NSMedieval.Goap.IDamageCommonAgent");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Controllers.CombatController", "OnAgentKilled", "NSMedieval.Goap.IDamageDealAgent", "NSMedieval.Goap.IDamageTakingAgent");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Goap.Actions.CombatActions", "AttackMelee", "NSMedieval.Goap.IDamageDealAgent");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.Goap.Actions.CombatActions", "FireRangedWeapon", "NSMedieval.Goap.IDamageDealAgent");
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.ArrowTrajectory", "Hit", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.CreatureBase", "StartBleeding", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.CreatureBase", "StopBleeding", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.CreatureBase", "Faint", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.CreatureBase", "UnFaint", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.CreatureBase", "OnBloodDepleted", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.State.CreatureBase", "OnEffectorStartWoundsCheck", "NSMedieval.StatsSystem.StatEffector");
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.State.CreatureBase", "OnEffectorEndWoundsCheck", "NSMedieval.StatsSystem.StatEffector");
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.CreatureBase", "Dispose", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.CreatureBase", "FinalizeDispose", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.AnimalInstance", "OnHealthDepleted", new[] { typeof(bool) });
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.AnimalInstance", "FinalizeDispose", Type.EmptyTypes);
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.HumanoidInstance", "OnHealthDepleted", new[] { typeof(bool) });
            count += TryPatchReplicationCombatDiagnosticByTypeNames(harmony, postfix, "NSMedieval.State.HumanoidInstance", "OnKilled", "NSMedieval.Goap.IDamageDealAgent");
            count += TryPatchReplicationCombatDiagnostic(harmony, postfix, "NSMedieval.State.HumanoidInstance", "FinalizeDispose", Type.EmptyTypes);

            LogReplicationInfo("Going Cooperative combat diagnostics installed patches="
                + count.ToString(CultureInfo.InvariantCulture)
                + " mutationGates master=" + replicationConfigCombatReplication
                + " draft=" + replicationConfigCombatDraftCommands
                + " attack=" + replicationConfigCombatAttackCommands
                + " state=" + replicationConfigCombatStateReplication
                + " health=" + replicationConfigCombatHealthReplication
                + " healthDetail=" + replicationConfigCombatHealthDetailReplication
                + " death=" + replicationConfigCombatDeathReplication
                + " presentation=" + replicationConfigCombatPresentationReplication
                + " projectiles=" + replicationConfigCombatProjectileReplication
                + " externalAgents=" + replicationConfigCombatExternalAgentLifecycle);
        }

        private int TryPatchReplicationCombatDiagnosticByTypeNames(
            Harmony harmony,
            HarmonyMethod postfix,
            string typeName,
            string methodName,
            params string[] parameterTypeNames)
        {
            var parameterTypes = new Type[parameterTypeNames.Length];
            for (var i = 0; i < parameterTypeNames.Length; i++)
            {
                var parameterType = AccessTools.TypeByName(parameterTypeNames[i]);
                if (parameterType == null)
                {
                    LogReplicationWarning("Going Cooperative combat diagnostic parameter type missing method="
                        + typeName + "." + methodName + " parameter=" + parameterTypeNames[i]);
                    return 0;
                }
                parameterTypes[i] = parameterType;
            }
            return TryPatchReplicationCombatDiagnostic(harmony, postfix, typeName, methodName, parameterTypes);
        }

        private int TryPatchReplicationCombatDiagnostic(
            Harmony harmony,
            HarmonyMethod postfix,
            string typeName,
            string methodName,
            Type[] parameterTypes)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.Method(type, methodName, parameterTypes);
                if (method == null)
                {
                    LogReplicationWarning("Going Cooperative combat diagnostic method missing " + typeName + "." + methodName);
                    return 0;
                }
                harmony.Patch(method, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative combat diagnostic patch failed "
                    + typeName + "." + methodName + " " + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private static void ReplicationCombatDiagnosticPostfix(MethodBase __originalMethod, object? __instance, object[]? __args)
        {
            if (!replicationConfigCombatDiagnostics || !replicationRuntimeStarted || !replicationRemoteHelloReceived) return;
            var builder = new StringBuilder(320);
            builder.Append("Going Cooperative combat trace side=")
                .Append(replicationConfigHostMode ? "host" : "client")
                .Append(" method=")
                .Append(__originalMethod?.DeclaringType?.FullName ?? "<unknown>")
                .Append('.')
                .Append(__originalMethod?.Name ?? "<unknown>")
                .Append(" instance=")
                .Append(FormatReplicationCombatDiagnosticValue(__instance));
            var args = __args ?? Array.Empty<object>();
            for (var i = 0; i < args.Length; i++)
            {
                builder.Append(" arg").Append(i).Append('=').Append(FormatReplicationCombatDiagnosticValue(args[i]));
            }
            instance?.LogReplicationInfo(builder.ToString());
        }

        private static string FormatReplicationCombatDiagnosticValue(object? value)
        {
            if (value == null) return "<null>";
            var type = value.GetType();
            var builder = new StringBuilder(160);
            if (TryGetReplicationAgentOwnerEntityId(value, out var entityId, out _))
            {
                builder.Append(entityId).Append(':');
            }
            builder.Append(FormatShortTypeName(type));
            AppendReplicationCombatDiagnosticMember(builder, value, "Damage", "damage");
            AppendReplicationCombatDiagnosticMember(builder, value, "ArmorDamage", "armor");
            AppendReplicationCombatDiagnosticMember(builder, value, "Critical", "critical");
            AppendReplicationCombatDiagnosticMember(builder, value, "IsWounded", "wounded");
            AppendReplicationCombatDiagnosticMember(builder, value, "IsBleeding", "bleeding");
            AppendReplicationCombatDiagnosticMember(builder, value, "HasDied", "died");
            AppendReplicationCombatDiagnosticMember(builder, value, "HasFainted", "fainted");
            if (TryResolveReplicationBehaviourOwner(value, out var behaviour))
            {
                AppendReplicationCombatDiagnosticMember(builder, behaviour!, "IsDrafting", "drafting");
                AppendReplicationCombatDiagnosticMember(builder, behaviour!, "CombatMode", "combatMode");
            }
            AppendReplicationCombatDiagnosticStat(builder, value, "Health");
            AppendReplicationCombatDiagnosticStat(builder, value, "Blood");
            AppendReplicationCombatDiagnosticStat(builder, value, "Consciousness");
            AppendReplicationCombatDiagnosticStat(builder, value, "Pain");
            return builder.ToString();
        }

        private static void AppendReplicationCombatDiagnosticStat(StringBuilder builder, object? owner, string statName)
        {
            if (owner != null && TryReadCombatStat(owner, statName, out var current))
            {
                builder.Append(';').Append(statName.ToLowerInvariant()).Append('=').Append(current.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        private static void AppendReplicationCombatDiagnosticMember(StringBuilder builder, object value, string memberName, string label)
        {
            if (TryReadInstanceMemberValue(value, memberName, out var member) && member != null)
            {
                builder.Append(';').Append(label).Append('=').Append(Convert.ToString(member, CultureInfo.InvariantCulture));
            }
        }
    }
}
