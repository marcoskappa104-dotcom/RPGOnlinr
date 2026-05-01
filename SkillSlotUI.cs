using UnityEngine;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// NetworkMonsterSpawner — versão online do spawner.
    /// Requer NetworkIdentity no GameObject.
    /// Só spawna no servidor. Os clientes recebem automaticamente via Mirror.
    /// Use este em vez do MonsterSpawner quando estiver em modo online.
    /// </summary>
    public class NetworkMonsterSpawner : NetworkBehaviour
    {
        [System.Serializable]
        public class SpawnGroup
        {
            public GameObject  monsterPrefab; // deve ter NetworkIdentity
            public Transform[] spawnPoints;
        }

        [SerializeField] private SpawnGroup[] spawnGroups;

        public override void OnStartServer()
        {
            foreach (var group in spawnGroups)
            {
                if (group.monsterPrefab == null) continue;
                foreach (var sp in group.spawnPoints)
                {
                    var mob = Instantiate(group.monsterPrefab, sp.position, sp.rotation);
                    NetworkServer.Spawn(mob);
                }
            }
            Debug.Log("[NetworkMonsterSpawner] Monstros spawnados no servidor.");
        }
    }
}
