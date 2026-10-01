using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// NpcId -> NpcPersona の解決を行うレジストリの最小インターフェース例。
    /// 実際はScriptableObjectやDBから読み込む実装を想定。
    /// </summary>
    public class NpcRegistry : MonoBehaviour
    {
        [SerializeField] private List<NpcPersona> _personas = new List<NpcPersona>();
        private Dictionary<string, NpcPersona> _map;

        private void Awake()
        {
            _map = _personas.ToDictionary(p => p.NpcId, p => p);
        }

        public NpcPersona Resolve(string npcId)
        {
            if (_map == null) Awake();
            return _map.TryGetValue(npcId, out var p) ? p : null;
        }

        /// <summary>
        /// 登録済みの全ペルソナ（デバッグUI等で、まだキャッシュが無いNPCも一覧表示するために使う）。
        /// </summary>
        public IReadOnlyList<NpcPersona> AllPersonas
        {
            get
            {
                if (_map == null) Awake();
                return _personas;
            }
        }
    }
}
