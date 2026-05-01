using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayer — representa um jogador na rede.
    /// 
    /// Sincroniza via Mirror:
    ///   - Posição e rotação (NetworkTransformReliable)
    ///   - Nome do personagem, raça, nível (SyncVar — visto por todos)
    ///   - HP atual (SyncVar — para a barra de HP de outros players)
    ///   - Animações (SyncVar de estado)
    /// 
    /// Lógica de input e movimento SÓ roda no cliente dono (isLocalPlayer).
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour
    {
        // ── SyncVars — sincronizadas automaticamente server→clients ──────
        [SyncVar(hook = nameof(OnNameChanged))]
        public string CharacterName = "...";

        [SyncVar(hook = nameof(OnRaceChanged))]
        public string Race = "Human";

        [SyncVar]
        public int Level = 1;

        [SyncVar(hook = nameof(OnHPChanged))]
        public float CurrentHP = 100f;

        [SyncVar]
        public float MaxHP = 100f;

        [SyncVar(hook = nameof(OnMovingChanged))]
        public bool IsMoving = false;

        // ── Componentes ───────────────────────────────────────────────────
        private NavMeshAgent    _agent;
        private Animator        _animator;

        [Header("Visuals")]
        [SerializeField] private GameObject      localIndicator;   // seta/coroa sobre o próprio player
        [SerializeField] private GameObject      nameTagRoot;
        [SerializeField] private TMPro.TMP_Text  nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        // ── Dados locais (só no dono) ─────────────────────────────────────
        private CharacterData _charData;
        private RPG.Combat.SkillSystem _skillSystem;
        private float _moveCheckTimer;

        // ── Unity ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent      = GetComponent<NavMeshAgent>();
            _animator   = GetComponentInChildren<Animator>();
            _skillSystem = GetComponent<RPG.Combat.SkillSystem>();
        }

        // Chamado pelo Mirror quando o objeto é spawned em TODOS os clientes
        public override void OnStartClient()
        {
            // Mostra o nome de todos os players
            if (nameTagText != null)
                nameTagText.text = CharacterName;

            // Indicador visual só para o dono local
            if (localIndicator != null)
                localIndicator.SetActive(isLocalPlayer);
        }

        // Chamado apenas no cliente que é dono deste objeto
        public override void OnStartLocalPlayer()
        {
            Debug.Log($"[NetworkPlayer] Você entrou como: {CharacterName}");

            // Carrega dados do personagem selecionado
            _charData = GameManager.Instance?.SelectedCharacter;
            if (_charData == null) return;

            // Envia dados ao servidor para sincronizar com todos
            CmdSetCharacterInfo(
                _charData.CharacterName,
                _charData.Race.ToString(),
                _charData.Level,
                _charData.CurrentHP,
                _charData.GetDerivedStats().MaxHP
            );

            // Inicializa componentes locais
            var playerEntity = GetComponent<RPG.Character.PlayerEntity>();
            playerEntity?.Initialize(_charData);

            // Conecta câmera ao player local
            var cam = FindObjectOfType<RPG.Systems.CameraController>();
            cam?.SetTarget(transform);

            // Conecta HUD local
            UIManager.Instance?.BindLocalPlayer(playerEntity);
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            // Sincroniza estado de movimento para todos verem a animação
            _moveCheckTimer += Time.deltaTime;
            if (_moveCheckTimer >= 0.1f)
            {
                _moveCheckTimer = 0f;
                bool moving = _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != IsMoving)
                    CmdSetMoving(moving);
            }

            // Atualiza animação local
            UpdateAnimations(_agent.velocity.sqrMagnitude > 0.05f);
        }

        // ── Commands (Cliente → Servidor) ─────────────────────────────────

        [Command]
        private void CmdSetCharacterInfo(string charName, string race, int level, float hp, float maxHp)
        {
            CharacterName = charName;
            Race          = race;
            Level         = level;
            CurrentHP     = hp;
            MaxHP         = maxHp;
        }

        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            // Servidor valida e executa o movimento
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        [Command]
        public void CmdSetMoving(bool moving)
        {
            IsMoving = moving;
        }

 [Server]
public void ServerApplyDamage(float amount)
{
    CurrentHP = Mathf.Max(0, CurrentHP - amount);
    // O hook OnHPChanged dispara automaticamente para todos os clientes
}

        [Command]
        public void CmdSyncHP(float hp, float maxHp)
        {
            CurrentHP = hp;
            MaxHP     = maxHp;
        }

        // ── ClientRpc (Servidor → Todos os Clientes) ──────────────────────

        [ClientRpc]
        public void RpcPlayAnimation(string trigger)
        {
            _animator?.SetTrigger(trigger);
        }

        // ── Hooks de SyncVar ──────────────────────────────────────────────

        private void OnNameChanged(string oldName, string newName)
        {
            if (nameTagText != null) nameTagText.text = newName;
        }

        private void OnRaceChanged(string oldRace, string newRace)
        {
            // Futuramente: trocar modelo 3D baseado na raça
        }

        private void OnHPChanged(float oldHP, float newHP)
        {
            // Atualiza barra de HP acima do player (visível para outros)
            if (hpBarSlider != null)
            {
                hpBarSlider.maxValue = MaxHP;
                hpBarSlider.value    = newHP;
                // Mostra barra só se não tiver HP cheio
                hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }
        }

        private void OnMovingChanged(bool oldVal, bool newVal)
        {
            if (!isLocalPlayer) UpdateAnimations(newVal);
        }

        // ── Animações ─────────────────────────────────────────────────────

        private void UpdateAnimations(bool moving)
        {
            if (_animator == null) return;
            _animator.SetBool("IsMoving", moving);
        }

        // ── ITargetable para outros players ──────────────────────────────

        // NetworkPlayer pode ser selecionado como alvo
        public string  DisplayName => CharacterName;
        public float   HPCurrent   => CurrentHP;
        public float   HPMax       => MaxHP;
        public bool    Dead        => CurrentHP <= 0;
    }
}
