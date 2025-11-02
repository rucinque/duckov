using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Duckov.Modding; // S1: ModBehaviour base class documentation
using UnityEngine;

namespace StatBuffMod
{
    /// <summary>
    /// Dynamic mod behaviour that registers and grants a flat-stat totem.
    /// Verification guidance:
    /// 1) Character screen max HP should increase by +50 once the Totem_StatBuff is equipped. // Derived from S2
    /// 2) Head and body armor values should each show +2.4 after equipping the totem. // Derived from S3 & S4
    /// </summary>
    public sealed class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private ScriptableObject _dynamicTotem;
        private bool _registrationLogged;
        private bool _grantResultLogged;

        public override void Start()
        {
            base.Start();
            Debug.Log("[StatBuffMod] ModBehaviour.Start() invoked.");
            StartCoroutine(InitializeRoutine());
        }

        private IEnumerator InitializeRoutine()
        {
            // Ensure the custom totem is registered before attempting to give it to the player. // S1: custom item injection guidance
            while (!EnsureTotemRegistered())
            {
                yield return null;
            }

            // Allow the scene to finish spawning player/inventory subsystems.
            for (var i = 0; i < 10; i++)
            {
                yield return null;
            }

            yield return TryGrantTotemToPlayer();
        }

        private bool EnsureTotemRegistered()
        {
            if (_dynamicTotem != null)
            {
                return true;
            }

            try
            {
                var itemType = FindType(new[]
                {
                    "ItemStatsSystem.Item, ItemStatsSystem",
                    "ItemStatsSystem.GameItem, ItemStatsSystem"
                });

                if (itemType == null)
                {
                    if (!_registrationLogged)
                    {
                        Debug.LogWarning("[StatBuffMod] Unable to locate ItemStatsSystem.Item type. Dynamic totem cannot be created.");
                        _registrationLogged = true;
                    }

                    return false;
                }

                var created = ScriptableObject.CreateInstance(itemType);
                if (created == null)
                {
                    if (!_registrationLogged)
                    {
                        Debug.LogError("[StatBuffMod] ScriptableObject.CreateInstance returned null for ItemStatsSystem.Item.");
                        _registrationLogged = true;
                    }

                    return false;
                }

                ConfigureCoreFields(created, itemType);

                if (!ApplyModifiers(created, itemType))
                {
                    return false;
                }

                if (!RegisterDynamicEntry(created, itemType))
                {
                    return false;
                }

                _dynamicTotem = created;
                Debug.Log("[StatBuffMod] Registered Totem_StatBuff dynamic item with three flat stat modifiers.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StatBuffMod] Exception while creating dynamic totem: {ex}");
                return false;
            }
        }

        private void ConfigureCoreFields(ScriptableObject instance, Type itemType)
        {
            TrySetMember(instance, itemType, "name", "Totem_StatBuff");
            TrySetMember(instance, itemType, "Name", "Totem_StatBuff");
            TrySetMember(instance, itemType, "Category", "Totem"); // S1: Totem category for custom items
            TrySetMember(instance, itemType, "PrefabPath", string.Empty);
            TrySetMember(instance, itemType, "DisplayName", "Totem_StatBuff");
            TrySetMember(instance, itemType, "Description", "Stat buffer totem (flat bonuses).");
            TrySetMember(instance, itemType, "MaxDurability", 1f);
            TrySetMember(instance, itemType, "Durability", 1f);
            TrySetMember(instance, itemType, "StackLimit", 1);
            TrySetMember(instance, itemType, "Sticky", true);

            var tags = new[] { "Totem", "DontDropOnDeadInSlot" }; // S1 & base-game totem tags
            AssignTags(instance, itemType, tags);
        }

        private bool ApplyModifiers(ScriptableObject instance, Type itemType)
        {
            var assembly = itemType.Assembly;
            var modifierType = FindType(assembly, new[]
            {
                "ItemStatsSystem.Modifier",
                "ItemStatsSystem.ItemModifier",
                "ItemStatsSystem.StatModifier"
            });

            if (modifierType == null)
            {
                Debug.LogError("[StatBuffMod] Unable to locate modifier data type; modifiers will not apply.");
                return false;
            }

            var modifiers = Array.CreateInstance(modifierType, 3);
            modifiers.SetValue(CreateModifier(modifierType, "Stat_MaxHealth", 50f), 0); // S2: Stat_MaxHealth, Type:0, Target:2
            modifiers.SetValue(CreateModifier(modifierType, "Stat_BodyArmor", 2.4f), 1); // S4: Stat_BodyArmor, Type:0, Target:2
            modifiers.SetValue(CreateModifier(modifierType, "Stat_HeadArmor", 2.4f), 2); // S3: Stat_HeadArmor, Type:0, Target:2

            if (!TrySetMember(instance, itemType, "Modifiers", modifiers))
            {
                Debug.LogError("[StatBuffMod] Unable to assign modifiers array to totem definition.");
                return false;
            }

            return true;
        }

        private object CreateModifier(Type modifierType, string displayNameKey, float value)
        {
            var modifier = Activator.CreateInstance(modifierType);
            TrySetMember(modifier, modifierType, "DisplayNameKey", displayNameKey);
            TrySetMember(modifier, modifierType, "LocalizationKey", displayNameKey);
            TrySetMember(modifier, modifierType, "Value", value);
            TrySetMember(modifier, modifierType, "Target", 2); // S2-S4: character stats use Target=2
            TrySetMember(modifier, modifierType, "Type", 0); // S2-S4: flat modifiers are Type=0 (contrast with S5 percentage Type=100 examples)
            TrySetMember(modifier, modifierType, "Operation", 0); // fallback if Operation property exists
            return modifier;
        }

        private bool RegisterDynamicEntry(ScriptableObject instance, Type itemType)
        {
            var collectionType = FindType(new[]
            {
                "ItemStatsSystem.ItemAssetsCollection, ItemStatsSystem",
                "ItemStatsSystem.ItemDatabase, ItemStatsSystem"
            });

            if (collectionType == null)
            {
                Debug.LogError("[StatBuffMod] Unable to locate ItemAssetsCollection; cannot register dynamic totem.");
                return false;
            }

            try
            {
                var addMethod = collectionType.GetMethod(
                    "AddDynamicEntry",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                    null,
                    new[] { itemType },
                    null);

                object collectionInstance = null;
                if (addMethod == null)
                {
                    // Attempt to find an overload with generic object parameter.
                    addMethod = collectionType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "AddDynamicEntry" && m.GetParameters().Length == 1);
                }

                if (addMethod == null)
                {
                    Debug.LogError("[StatBuffMod] AddDynamicEntry method not found; dynamic totem cannot be registered.");
                    return false;
                }

                if (!addMethod.IsStatic)
                {
                    var instanceProperty = collectionType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    collectionInstance = instanceProperty?.GetValue(null);
                    if (collectionInstance == null)
                    {
                        Debug.LogWarning("[StatBuffMod] ItemAssetsCollection.Instance is null; attempting to create new instance via Activator.");
                        collectionInstance = Activator.CreateInstance(collectionType);
                    }
                }

                addMethod.Invoke(collectionInstance, new[] { instance });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StatBuffMod] Failed to register totem via AddDynamicEntry: {ex}");
                return false;
            }
        }

        private IEnumerator TryGrantTotemToPlayer()
        {
            const int maxAttempts = 10;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (TryGrantTotemInternal())
                {
                    yield break;
                }

                yield return new WaitForSeconds(1f);
            }

            if (!_grantResultLogged)
            {
                Debug.LogWarning("[StatBuffMod] Could not automatically place Totem_StatBuff into the inventory. Please acquire it from inventory debug tools and equip manually.");
                _grantResultLogged = true;
            }
        }

        private bool TryGrantTotemInternal()
        {
            if (_dynamicTotem == null)
            {
                return false;
            }

            var playerType = FindType(new[]
            {
                "Duckov.Player.PlayerController, Assembly-CSharp",
                "Duckov.Characters.Player.PlayerController, Assembly-CSharp",
                "TeamSoda.Duckov.Core.Player.PlayerController, TeamSoda.Duckov.Core"
            });

            if (playerType == null)
            {
                if (!_grantResultLogged)
                {
                    Debug.LogWarning("[StatBuffMod] Player controller type not found; cannot auto-grant totem.");
                    _grantResultLogged = true;
                }

                return false;
            }

            var players = UnityEngine.Object.FindObjectsOfType(playerType);
            if (players == null || players.Length == 0)
            {
                return false;
            }

            var player = players.GetValue(0);
            var inventory = GetMemberValue(player, playerType, new[] { "Inventory", "inventory", "PlayerInventory" });
            if (inventory == null)
            {
                return false;
            }

            var inventoryType = inventory.GetType();
            var itemInstance = CreateItemInstance(inventoryType.Assembly) ?? _dynamicTotem;

            if (itemInstance is not null)
            {
                TrySetMember(itemInstance, itemInstance.GetType(), "Item", _dynamicTotem);
                TrySetMember(itemInstance, itemInstance.GetType(), "Definition", _dynamicTotem);
                TrySetMember(itemInstance, itemInstance.GetType(), "Data", _dynamicTotem);
                TrySetMember(itemInstance, itemInstance.GetType(), "Quantity", 1);
                TrySetMember(itemInstance, itemInstance.GetType(), "StackSize", 1);
            }

            var grantSuccess = InvokeAddToInventory(inventory, inventoryType, itemInstance);
            if (grantSuccess)
            {
                Debug.Log("[StatBuffMod] Totem_StatBuff granted; check Totem slot to verify bonuses (MaxHealth +50, Body/Head Armor +2.4 each). // S2-S4");
                _grantResultLogged = true;
                return true;
            }

            return false;
        }

        private object CreateItemInstance(Assembly assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            var candidateTypes = new[]
            {
                "ItemStatsSystem.ItemInstance",
                "ItemStatsSystem.InventoryItem",
                "TeamSoda.Duckov.Core.Inventory.InventoryItem"
            };

            foreach (var name in candidateTypes)
            {
                var type = FindType(assembly, name);
                if (type == null)
                {
                    continue;
                }

                try
                {
                    return Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[StatBuffMod] Failed to create {name}: {ex.Message}");
                }
            }

            return null;
        }

        private bool InvokeAddToInventory(object inventory, Type inventoryType, object itemInstance)
        {
            var methods = inventoryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var method in methods)
            {
                if (!method.Name.Contains("Add", StringComparison.OrdinalIgnoreCase) &&
                    !method.Name.Contains("Give", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    continue;
                }

                var args = new object[parameters.Length];
                var matched = false;

                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameterType = parameters[i].ParameterType;
                    if (!matched && itemInstance != null && parameterType.IsInstanceOfType(itemInstance))
                    {
                        args[i] = itemInstance;
                        matched = true;
                    }
                    else if (!matched && parameterType.IsInstanceOfType(_dynamicTotem))
                    {
                        args[i] = _dynamicTotem;
                        matched = true;
                    }
                    else if (parameterType == typeof(int))
                    {
                        args[i] = 1;
                    }
                    else if (parameterType == typeof(bool))
                    {
                        args[i] = true;
                    }
                    else if (parameterType == typeof(string))
                    {
                        args[i] = "Totem";
                    }
                    else
                    {
                        args[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
                    }
                }

                if (!matched)
                {
                    continue;
                }

                try
                {
                    var result = method.Invoke(inventory, args);
                    if (result is bool boolResult)
                    {
                        return boolResult;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[StatBuffMod] Failed to invoke {method.Name}: {ex.Message}");
                }
            }

            return false;
        }

        private void AssignTags(object instance, Type type, IEnumerable<string> tags)
        {
            if (TrySetMember(instance, type, "Tags", tags.ToArray()))
            {
                return;
            }

            var listType = typeof(List<string>);
            var tagList = Activator.CreateInstance(listType) as IList<string>;
            if (tagList != null)
            {
                foreach (var tag in tags)
                {
                    tagList.Add(tag);
                }

                if (TrySetMember(instance, type, "Tags", tagList))
                {
                    return;
                }
            }

            TrySetMember(instance, type, "tags", tags.ToArray());
        }

        private bool TrySetMember(object target, Type targetType, string memberName, object value)
        {
            if (target == null)
            {
                return false;
            }

            var property = targetType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (property != null && property.CanWrite)
            {
                var converted = ConvertValue(property.PropertyType, value);
                property.SetValue(target, converted);
                return true;
            }

            var field = targetType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (field != null)
            {
                var converted = ConvertValue(field.FieldType, value);
                field.SetValue(target, converted);
                return true;
            }

            return false;
        }

        private object ConvertValue(Type targetType, object value)
        {
            if (value == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType.IsInstanceOfType(value))
            {
                return value;
            }

            try
            {
                if (targetType == typeof(float))
                {
                    return Convert.ToSingle(value);
                }

                if (targetType == typeof(double))
                {
                    return Convert.ToDouble(value);
                }

                if (targetType == typeof(int))
                {
                    return Convert.ToInt32(value);
                }

                if (targetType == typeof(bool))
                {
                    return Convert.ToBoolean(value);
                }

                if (targetType.IsEnum)
                {
                    return Enum.ToObject(targetType, value);
                }

                if (targetType == typeof(string))
                {
                    return value.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[StatBuffMod] Failed to convert value for {targetType.FullName}: {ex.Message}");
            }

            return value;
        }

        private object GetMemberValue(object instance, Type type, IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (property != null)
                {
                    var value = property.GetValue(instance);
                    if (value != null)
                    {
                        return value;
                    }
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    var value = field.GetValue(instance);
                    if (value != null)
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private Type FindType(IEnumerable<string> qualifiedNames)
        {
            foreach (var name in qualifiedNames)
            {
                var type = Type.GetType(name);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private Type FindType(Assembly assembly, IEnumerable<string> candidateNames)
        {
            if (assembly == null)
            {
                return null;
            }

            foreach (var name in candidateNames)
            {
                var type = assembly.GetType(name);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private Type FindType(Assembly assembly, string candidateName)
        {
            return assembly?.GetType(candidateName);
        }
    }
}
