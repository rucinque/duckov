using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Duckov.Modding;

/*
  DuckovStatTweaks.ModBehaviour

  搜索记录（离线仓库检索，2025-11-01 UTC）：
  - Step A1: 枚举指定 Managed 目录。仅 Duckov_Data/Managed 存在；duckov_Data/Managed 与 Duckov.app/... 未出现。
  - Step A2: 在 Duckov_Data/Managed 目录下找到 TeamSoda.Duckov.Core.dll、TeamSoda.Duckov.Utilities.dll、ItemStatsSystem.dll 以及大量 UnityEngine.*.dll；未发现 Duckov.Modding.dll —— 但注意当前仓库的 dll 为 Git LFS 指针，无法在离线环境中解包。
  - Step A3: 因为 dll 是 LFS 指针，本地无法离线反射，因此把候选类型扫描放在运行时（Mini-Mod）中执行，并将结果写入 [DuckovStatScanner] 日志。

  真实名称决议表（待运行时解锁）：
  - Max HP 字段链：未确认（猜测 PlayerStats.Health.Max 或 HealthComponent.MaxHealth；运行时扫描确认）。
  - 当前 HP 字段链：未确认（候选 Current/Value/HP）。
  - 头部护甲字段链：未确认（候选 Armor.Head.Rating / HeadArmor）。
  - 身体护甲字段链：未确认（候选 Armor.Body.Rating / BodyArmor）。
  - 伤害入口事件：未确认（候选 OnBeforeDamageApplied / OnCalculateDamage；若未命中则启用兜底回填算法）。

  运行说明：首次运行会在 Start() 内触发 DuckovStatScanner 日志，把已加载程序集中的 Health/Armor/Damage 相关类型与字段记录到
  Application.persistentDataPath 下的 DuckovStatScanner_*.log 文件。实际字段名确认后可把表更新为“已确认”并关闭扫描。
*/

namespace DuckovStatTweaks
{
  public class ModBehaviour : Duckov.Modding.ModBehaviour
  {
    const float EXTRA_HP = 40f;
    const float ARMOR_ADD = 1.5f;
    const float PHYS_DR = 0.15f;

    static readonly BindingFlags BF_All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    bool _applied;
    bool _hpBoosted;
    bool _headArmorBoosted;
    bool _bodyArmorBoosted;
    bool _hookActive;
    bool _backfillEnabled;
    bool _scanAttempted;

    float _lastHp = -1f;

    Component _hpComp;
    Type _healthType;

    string _scanLogPath;

    void Start()
    {
      DontDestroyOnLoad(gameObject);
      RunDiagnosticScan();
      StartCoroutine(ApplyWhenReady());
    }

    void RunDiagnosticScan()
    {
      if (_scanAttempted)
        return;
      _scanAttempted = true;

      const string prefix = "[DuckovStatScanner]";
      try
      {
        var sb = new StringBuilder();
        sb.AppendLine(prefix + " DuckovStatTweaks runtime scanner engaged.");
        sb.AppendLine(prefix + " Unity persistentDataPath = " + Application.persistentDataPath);

        // 记录 Managed 目录是否存在（兼容不同平台）
        var gameRoot = Path.GetDirectoryName(Application.dataPath);
        var managedCandidates = new[]
        {
          Path.Combine(gameRoot ?? string.Empty, "Duckov_Data", "Managed"),
          Path.Combine(gameRoot ?? string.Empty, "duckov_Data", "Managed"),
          Path.Combine(gameRoot ?? string.Empty, "Duckov.app", "Contents", "Resources", "Data", "Managed")
        };
        foreach (var path in managedCandidates)
        {
          sb.AppendLine(prefix + " Managed path check: " + path + " => " + (Directory.Exists(path) ? "exists" : "missing"));
        }

        // 记录目标程序集
        var targets = new[]
        {
          "Duckov.Modding",
          "TeamSoda.Duckov.Core",
          "TeamSoda.Duckov.Utilities",
          "ItemStatsSystem",
          "Assembly-CSharp"
        };

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
          var name = asm.GetName().Name;
          if (targets.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
          {
            sb.AppendLine(prefix + " Loaded assembly => " + name);
          }
        }

        // 扫描候选类型与字段
        AppendTypeScan(sb, prefix, "Health", type =>
        {
          var rows = new List<string>();
          foreach (var member in type.GetMembers(BF_All))
          {
            if (MemberLooksRelevant(member, new[] { "Max", "Current", "Value", "Health" }))
            {
              rows.Add(MemberSummary(member));
            }
          }
          return rows;
        });

        AppendTypeScan(sb, prefix, "Armor", type =>
        {
          var rows = new List<string>();
          foreach (var member in type.GetMembers(BF_All))
          {
            if (MemberLooksRelevant(member, new[] { "Head", "Body", "Armor", "Rating", "Value" }))
            {
              rows.Add(MemberSummary(member));
            }
          }
          return rows;
        });

        AppendTypeScan(sb, prefix, "Damage", type =>
        {
          var rows = new List<string>();
          foreach (var member in type.GetMembers(BF_All))
          {
            if (MemberLooksRelevant(member, new[] { "Damage", "On", "Physical", "Type" }))
            {
              rows.Add(MemberSummary(member));
            }
          }
          return rows;
        });

        var fileName = $"DuckovStatScanner_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        _scanLogPath = Path.Combine(Application.persistentDataPath ?? string.Empty, fileName);
        File.WriteAllText(_scanLogPath, sb.ToString());
        Debug.Log(prefix + " Wrote diagnostic log to " + _scanLogPath);
      }
      catch (Exception ex)
      {
        Debug.LogWarning(prefix + " Scan failed: " + ex);
      }
    }

    IEnumerator ApplyWhenReady()
    {
      var deadline = Time.time + 20f;
      while (Time.time < deadline && !_applied)
      {
        TryApplyPatches();
        if (!_applied)
        {
          yield return new WaitForSeconds(1f);
        }
      }

      _backfillEnabled = true;
      Debug.Log("[DuckovStatTweaks] Backfill enabled = " + _backfillEnabled);
    }

    void Update()
    {
      if (!_backfillEnabled)
        return;

      if (_hookActive)
        return; // Hook 成功则不需要兜底

      EnsureHealthComponent();
      if (_hpComp == null)
        return;

      float cur = ReadFloat(_hpComp, new[] { "Current", "current", "Value", "value", "HP", "hp" });
      float max = ReadFloat(_hpComp, new[] { "Max", "max", "MaxHealth", "maxHealth" });
      if (cur <= 0f || max <= 0f)
        return;

      if (_lastHp < 0f)
      {
        _lastHp = cur;
        return;
      }

      if (cur < _lastHp)
      {
        float delta = _lastHp - cur;
        float heal = delta * PHYS_DR;
        AddFloat(_hpComp, new[] { "Current", "current", "Value", "value", "HP", "hp" }, heal);
        float after = ReadFloat(_hpComp, new[] { "Current", "current", "Value", "value", "HP", "hp" });
        Debug.Log("[DuckovStatTweaks] Backfill heal applied: +" + heal.ToString("F2") + " => " + after.ToString("F2"));
      }

      _lastHp = ReadFloat(_hpComp, new[] { "Current", "current", "Value", "value", "HP", "hp" });
    }

    void TryApplyPatches()
    {
      EnsureHealthComponent();

      if (!_hpBoosted)
      {
        _hpBoosted = TryBoostHealth();
      }

      if (!_headArmorBoosted)
      {
        _headArmorBoosted = TryBoostArmor(new[] { "HeadArmor", "ArmorHead", "armorHead", "DefenseHead" }, new[] { "Armor", "Head", "Rating" });
      }

      if (!_bodyArmorBoosted)
      {
        _bodyArmorBoosted = TryBoostArmor(new[] { "BodyArmor", "ArmorBody", "armorBody", "DefenseBody" }, new[] { "Armor", "Body", "Rating" });
      }

      if (!_hookActive)
      {
        _hookActive = TryHookDamageEvent();
      }

      _applied = _hpBoosted && _headArmorBoosted && _bodyArmorBoosted;
      Debug.Log($"[DuckovStatTweaks] Applied -> HP:{_hpBoosted}, Head+1.5:{_headArmorBoosted}, Body+1.5:{_bodyArmorBoosted}, Hook:{_hookActive}");
    }

    bool TryBoostHealth()
    {
      try
      {
        var player = LocatePlayer();
        bool hpOk =
          TryAddFloat(player, new[] { "MaxHealth", "maxHealth" }, EXTRA_HP) ||
          TryAddFloatChain(player, new[] { "Stats", "Health", "Max" }, EXTRA_HP) ||
          TryAcquireHealthAndAdd(EXTRA_HP);

        if (!hpOk)
          return false;

        EnsureHealthComponent();
        if (_hpComp != null)
        {
          float cur = ReadFloat(_hpComp, new[] { "Current", "current", "Value", "value", "HP", "hp" });
          float max = ReadFloat(_hpComp, new[] { "Max", "max", "MaxHealth", "maxHealth" });
          if (cur >= 0f && max > 0f)
          {
            float want = Mathf.Min(cur + EXTRA_HP, max);
            SetFloat(_hpComp, new[] { "Current", "current", "Value", "value", "HP", "hp" }, want);
            Debug.Log($"[DuckovStatTweaks] HP boosted: max += {EXTRA_HP}, current -> {want}");
          }
        }

        return true;
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[DuckovStatTweaks] HP boost failed: " + ex);
        return false;
      }
    }

    bool TryBoostArmor(string[] directNames, string[] chain)
    {
      try
      {
        var player = LocatePlayer();
        bool ok = TryAddFloat(player, directNames, ARMOR_ADD) || TryAddFloatChain(player, chain, ARMOR_ADD);
        if (ok)
        {
          Debug.Log("[DuckovStatTweaks] Armor boosted via " + string.Join("/", directNames) + " or " + string.Join(".", chain));
        }
        return ok;
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[DuckovStatTweaks] Armor boost failed: " + ex);
        return false;
      }
    }

    object LocatePlayer()
    {
      var playerType = FindTypeByAnyName(new[]
      {
        "TeamSoda.Duckov.Core.PlayerController",
        "TeamSoda.Duckov.Core.Player.PlayerController",
        "PlayerController",
        "Player",
        "PlayerStats"
      });

      if (playerType == null)
        return null;

      var singleton = GetSingleton(playerType);
      if (singleton != null)
        return singleton;

      var unityObject = FindUnityObjectByType(playerType);
      return unityObject;
    }

    bool TryAcquireHealthAndAdd(float add)
    {
      EnsureHealthComponent();
      if (_hpComp == null)
        return false;

      bool ok = AddFloat(_hpComp, new[] { "Max", "max", "MaxHealth", "maxHealth" }, add);
      if (ok)
      {
        Debug.Log("[DuckovStatTweaks] Health component max += " + add);
      }
      return ok;
    }

    void EnsureHealthComponent()
    {
      if (_hpComp != null)
        return;

      if (_healthType == null)
      {
        _healthType = FindTypeByAnyName(new[] { "Health", "PlayerHealth", "CharacterHealth" });
      }

      if (_healthType == null)
        return;

      var playerGo = GameObject.FindWithTag("Player") ?? GameObject.Find("Player");
      if (playerGo != null)
      {
        _hpComp = playerGo.GetComponent(_healthType);
      }

      if (_hpComp == null)
      {
        foreach (var go in GameObject.FindObjectsOfType<GameObject>())
        {
          var comp = go.GetComponent(_healthType);
          if (comp != null)
          {
            _hpComp = comp;
            break;
          }
        }
      }
    }

    bool TryHookDamageEvent()
    {
      try
      {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
          var types = SafeTypes(asm);
          foreach (var type in types)
          {
            if (!NameContains(type, new[] { "Damage", "Combat" }))
              continue;

            var events = type.GetEvents(BF_All);
            foreach (var ev in events)
            {
              if (NameContains(ev, new[] { "OnBeforeDamageApplied", "OnCalculateDamage", "DamageCalculated" }))
              {
                Debug.Log("[DuckovStatTweaks] Damage event candidate discovered: " + type.FullName + "." + ev.Name + " (hook not attached; signature pending runtime confirmation)");
                return false; // 未确认签名，避免误钩，保留兜底算法
              }
            }
          }
        }
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[DuckovStatTweaks] Hook error: " + ex.Message);
      }

      return false;
    }

    static bool NameContains(MemberInfo info, IEnumerable<string> keywords)
    {
      foreach (var keyword in keywords)
      {
        if (info.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
          return true;
      }
      return false;
    }

    static bool MemberLooksRelevant(MemberInfo member, IEnumerable<string> tokens)
    {
      foreach (var token in tokens)
      {
        if (member.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
          return true;
      }
      return false;
    }

    static string MemberSummary(MemberInfo member)
    {
      switch (member)
      {
        case FieldInfo fi:
          return $"FIELD {fi.FieldType.Name} {fi.Name}";
        case PropertyInfo pi:
          return $"PROP {pi.PropertyType.Name} {pi.Name}";
        case EventInfo ei:
          return $"EVENT {ei.EventHandlerType?.Name ?? "?"} {ei.Name}";
        case MethodInfo mi:
          return $"METHOD {mi.ReturnType.Name} {mi.Name}({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})";
        default:
          return member.MemberType + " " + member.Name;
      }
    }

    void AppendTypeScan(StringBuilder sb, string prefix, string keyword, Func<Type, List<string>> collector)
    {
      try
      {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
          foreach (var type in SafeTypes(asm))
          {
            if (!NameContains(type, new[] { keyword }))
              continue;

            sb.AppendLine(prefix + " Type hit => " + type.FullName);
            var rows = collector(type);
            foreach (var row in rows)
            {
              sb.AppendLine(prefix + "   " + row);
            }
          }
        }
      }
      catch (Exception ex)
      {
        sb.AppendLine(prefix + " Scan error for keyword " + keyword + ": " + ex.Message);
      }
    }

    static IEnumerable<Type> SafeTypes(Assembly asm)
    {
      try
      {
        return asm.GetTypes();
      }
      catch
      {
        return Array.Empty<Type>();
      }
    }

    static Type FindTypeByAnyName(string[] names)
    {
      foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
      {
        foreach (var name in names)
        {
          var type = asm.GetType(name, false);
          if (type != null)
            return type;
        }
      }

      foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
      {
        foreach (var type in SafeTypes(asm))
        {
          if (names.Any(n => type.FullName != null && type.FullName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
            return type;
        }
      }

      return null;
    }

    static object GetSingleton(Type type)
    {
      if (type == null)
        return null;

      var prop = type.GetProperty("Instance", BF_All);
      if (prop != null)
      {
        try
        {
          return prop.GetValue(null);
        }
        catch
        {
          return null;
        }
      }

      var field = type.GetField("Instance", BF_All);
      if (field != null)
      {
        try
        {
          return field.GetValue(null);
        }
        catch
        {
          return null;
        }
      }

      return null;
    }

    static object FindUnityObjectByType(Type type)
    {
      if (type == null)
        return null;

      var objects = UnityEngine.Object.FindObjectsOfType(type);
      if (objects != null && objects.Length > 0)
        return objects.GetValue(0);

      return null;
    }

    static bool TryAddFloat(object obj, string[] names, float add)
    {
      if (obj == null)
        return false;

      foreach (var name in names)
      {
        if (TryGetFloat(obj, name, out var value) && TrySetFloat(obj, name, value + add))
          return true;
      }

      return false;
    }

    static bool TryAddFloatChain(object obj, string[] chain, float add)
    {
      if (!ResolveChain(obj, chain, out var leafObj, out var leafName))
        return false;

      if (TryGetFloat(leafObj, leafName, out var value))
      {
        return TrySetFloat(leafObj, leafName, value + add);
      }

      return false;
    }

    static bool AddFloat(object obj, string[] names, float add)
    {
      foreach (var name in names)
      {
        if (TryGetFloat(obj, name, out var value) && TrySetFloat(obj, name, value + add))
          return true;
      }

      return false;
    }

    static bool ResolveChain(object obj, string[] chain, out object leafObj, out string leafName)
    {
      leafObj = obj;
      leafName = null;
      if (obj == null)
        return false;

      for (int i = 0; i < chain.Length; i++)
      {
        if (i == chain.Length - 1)
        {
          leafName = chain[i];
          return true;
        }

        leafObj = GetMember(leafObj, chain[i]);
        if (leafObj == null)
          return false;
      }

      return false;
    }

    static object GetMember(object obj, string name)
    {
      if (obj == null)
        return null;

      var type = obj.GetType();
      var prop = type.GetProperty(name, BF_All);
      if (prop != null)
      {
        try
        {
          return prop.GetValue(obj);
        }
        catch
        {
        }
      }

      var field = type.GetField(name, BF_All);
      if (field != null)
      {
        try
        {
          return field.GetValue(obj);
        }
        catch
        {
        }
      }

      return null;
    }

    static bool TryGetFloat(object obj, string name, out float value)
    {
      value = 0f;
      if (obj == null)
        return false;

      var type = obj.GetType();
      var prop = type.GetProperty(name, BF_All);
      if (prop != null)
      {
        try
        {
          var raw = prop.GetValue(obj);
          if (TryConvert(raw, out value))
            return true;
        }
        catch
        {
        }
      }

      var field = type.GetField(name, BF_All);
      if (field != null)
      {
        try
        {
          var raw = field.GetValue(obj);
          if (TryConvert(raw, out value))
            return true;
        }
        catch
        {
        }
      }

      return false;
    }

    static bool TrySetFloat(object obj, string name, float value)
    {
      if (obj == null)
        return false;

      var type = obj.GetType();
      var prop = type.GetProperty(name, BF_All);
      if (prop != null)
      {
        try
        {
          var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
          if (targetType == typeof(float) || targetType == typeof(double) || targetType == typeof(int))
          {
            object boxed = Convert.ChangeType(value, targetType);
            prop.SetValue(obj, boxed);
            return true;
          }
        }
        catch
        {
        }
      }

      var field = type.GetField(name, BF_All);
      if (field != null)
      {
        try
        {
          var targetType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
          if (targetType == typeof(float) || targetType == typeof(double) || targetType == typeof(int))
          {
            object boxed = Convert.ChangeType(value, targetType);
            field.SetValue(obj, boxed);
            return true;
          }
        }
        catch
        {
        }
      }

      return false;
    }

    static bool TryConvert(object value, out float result)
    {
      result = 0f;
      if (value == null)
        return false;

      try
      {
        result = Convert.ToSingle(value);
        return true;
      }
      catch
      {
        return false;
      }
    }

    static float ReadFloat(object obj, string[] names)
    {
      foreach (var name in names)
      {
        if (TryGetFloat(obj, name, out var value))
          return value;
      }

      return -1f;
    }

    static bool SetFloat(object obj, string[] names, float value)
    {
      foreach (var name in names)
      {
        if (TrySetFloat(obj, name, value))
          return true;
      }

      return false;
    }
  }
}
