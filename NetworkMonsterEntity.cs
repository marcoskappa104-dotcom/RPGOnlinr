using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using Mirror;
using RPG.Character;
using RPG.UI;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController — processa input APENAS no cliente local.
    /// Envia comandos ao servidor via CmdMoveTo.
    /// 
    /// Substitui o PlayerController offline quando em modo online.
    /// </summary>
    [RequireComponent(typeof(NetworkPlayer))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Layers")]
        [SerializeField] private LayerMask terrainLayer   = ~0;
        [SerializeField] private LayerMask targetableLayer;

        [Header("Skills")]
        [SerializeField] private KeyCode skill1Key = KeyCode.Q;
        [SerializeField] private KeyCode skill2Key = KeyCode.W;
        [SerializeField] private KeyCode skill3Key = KeyCode.E;
        [SerializeField] private KeyCode skill4Key = KeyCode.R;

        [Header("Move Indicator")]
        [SerializeField] private GameObject moveIndicatorPrefab;

        private NetworkPlayer            _netPlayer;
        private NavMeshAgent             _agent;
        private RPG.Combat.SkillSystem   _skills;

        private void Awake()
        {
            _netPlayer = GetComponent<NetworkPlayer>();
            _agent     = GetComponent<NavMeshAgent>();
            _skills    = GetComponent<RPG.Combat.SkillSystem>();
        }

        private void Update()
        {
            // Só processa input no cliente dono deste objeto
            if (!isLocalPlayer) return;

            HandleMouseInput();
            HandleSkillInput();
        }

        // ── Mouse ─────────────────────────────────────────────────────────

        private void HandleMouseInput()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            // 1. Verifica alvo (mob, NPC, outro player)
            if (targetableLayer != 0 &&
                Physics.Raycast(ray, out RaycastHit hitTarget, 300f, targetableLayer))
            {
                var targetable = hitTarget.collider.GetComponentInParent<ITargetable>();
                if (targetable != null && !targetable.IsDead)
                {
                    // Tenta selecionar NetworkPlayer de outro jogador
                    var otherNetPlayer = hitTarget.collider.GetComponentInParent<NetworkPlayer>();
                    if (otherNetPlayer != null && !otherNetPlayer.isLocalPlayer)
                    {
                        UIManager.Instance?.ShowMessage($"Selecionado: {otherNetPlayer.CharacterName} Lv{otherNetPlayer.Level}");
                        return;
                    }

                    var localPlayer = GetComponent<RPG.Character.PlayerEntity>();
                    localPlayer?.SetTarget(targetable);
                    UIManager.Instance?.UpdateTargetPanel(targetable);
                    return;
                }
            }

            // 2. Move no terreno
            if (Physics.Raycast(ray, out RaycastHit hitTerrain, 300f, terrainLayer))
            {
                Vector3 dest = hitTerrain.point;

                // Valida no NavMesh local antes de enviar
                if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 2f, NavMesh.AllAreas))
                    dest = navHit.position;

                // Movimento local imediato (responsivo)
                _agent.SetDestination(dest);

                // Sincroniza com o servidor
                _netPlayer.CmdMoveTo(dest);

                UIManager.Instance?.ClearTargetPanel();
                _skills?.CancelPendingAction();
                SpawnMoveIndicator(hitTerrain.point);
            }
        }

        private void HandleSkillInput()
        {
            if (Input.GetKeyDown(skill1Key)) UseSkill(0);
            if (Input.GetKeyDown(skill2Key)) UseSkill(1);
            if (Input.GetKeyDown(skill3Key)) UseSkill(2);
            if (Input.GetKeyDown(skill4Key)) UseSkill(3);
        }

        private void UseSkill(int index)
        {
            _skills?.TryUseSkill(index);
        }

        private void SpawnMoveIndicator(Vector3 pos)
        {
            if (moveIndicatorPrefab == null) return;
            var go = Instantiate(moveIndicatorPrefab, pos + Vector3.up * 0.02f, Quaternion.Euler(90, 0, 0));
            Destroy(go, 1f);
        }
    }
}
