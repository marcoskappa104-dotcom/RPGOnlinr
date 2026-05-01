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
    /// CORREÇÕES v2:
    ///   - Implementa ITargetable corretamente (OnSelected, OnDeselected, TakeDamage, IsDead)
    ///   - ServerApplyDamage agora sincroniza HP com todos via SyncVar E com o PlayerEntity local
    ///   - Removido CmdTakeDamage (Commands não podem ser chamados pelo servidor)
    ///   - RpcSyncDamageToLocal garante que o HUD do dono atualize ao tomar dano de monstros
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        // ── SyncVars ──────────────────────────────────────────────────────
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
        private NavMeshAgent _agent;
        private Animator     _animator;

        [Header("Visuals")]
        [SerializeField] private GameObject           localIndicator;
        [SerializeField] private GameObject           nameTagRoot;
        [SerializeField] private TMPro.TMP_Text       nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        [Header("Targetable")]
        [SerializeField] private GameObject selectionIndicator; // círculo no chão

        // ── Dados locais ──────────────────────────────────────────────────
        private CharacterData        _charData;
        private RPG.Combat.SkillSystem _skillSystem;
        private float                _moveCheckTimer;

        // ── ITargetable ───────────────────────────────────────────────────
        public string  DisplayName => CharacterName;
        public float   HPCurrent   => CurrentHP;   // mantido para compatibilidade
        public float   MaxHPValue  => MaxHP;        // mantido para compatibilidade

        // ITargetable interface
        float  ITargetable.CurrentHP => CurrentHP;
        float  ITargetable.MaxHP     => MaxHP;
        bool   ITargetable.IsDead    => CurrentHP <= 0f;
        Vector3 ITargetable.Position => transform.position;

        /// <summary>Propriedade de conveniência para verificar morte</summary>
        public bool Dead => CurrentHP <= 0f;

        public void OnSelected()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(true);
        }

        public void OnDeselected()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(false);
        }

        /// <summary>
        /// ITargetable.TakeDamage — chamado pelo SkillSystem de outro jogador.
        /// Redireciona para o servidor via Command (sem requiresAuthority para qualquer cliente chamar).
        /// </summary>
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (isServer)
            {
                ServerProcessDamage(rawAtk, rawMatk, isPhysical);
                return;
            }
            CmdRequestDamage(rawAtk, rawMatk, isPhysical);
        }

        // ── Unity ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent       = GetComponent<NavMeshAgent>();
            _animator    = GetComponentInChildren<Animator>();
            _skillSystem = GetComponent<RPG.Combat.SkillSystem>();
        }

        public override void OnStartClient()
        {
            if (nameTagText != null)
                nameTagText.text = CharacterName;

            if (localIndicator != null)
                localIndicator.SetActive(isLocalPlayer);

            if (selectionIndicator != null)
                selectionIndicator.SetActive(false);
        }

        public override void OnStartLocalPlayer()
        {
            Debug.Log($"[NetworkPlayer] Você entrou como: {CharacterName}");

            _charData = GameManager.Instance?.SelectedCharacter;
            if (_charData == null) return;

            CmdSetCharacterInfo(
                _charData.CharacterName,
                _charData.Race.ToString(),
                _charData.Level,
                _charData.CurrentHP,
                _charData.GetDerivedStats().MaxHP
            );

            var playerEntity = GetComponent<PlayerEntity>();
            playerEntity?.Initialize(_charData);

            var cam = FindObjectOfType<RPG.Systems.CameraController>();
            cam?.SetTarget(transform);

            UIManager.Instance?.BindLocalPlayer(playerEntity);
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            _moveCheckTimer += Time.deltaTime;
            if (_moveCheckTimer >= 0.1f)
            {
                _moveCheckTimer = 0f;
                bool moving = _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != IsMoving)
                    CmdSetMoving(moving);
            }

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
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        [Command]
        public void CmdSetMoving(bool moving)
        {
            IsMoving = moving;
        }

        /// <summary>
        /// Qualquer cliente pode pedir dano neste jogador (PvP, monstros, etc).
        /// O servidor valida e aplica.
        /// </summary>
        [Command(requiresAuthority = false)]
        private void CmdRequestDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            ServerProcessDamage(rawAtk, rawMatk, isPhysical);
        }

        [Command]
        public void CmdSyncHP(float hp, float maxHp)
        {
            CurrentHP = hp;
            MaxHP     = maxHp;
        }

        // ── Server Methods ────────────────────────────────────────────────

        /// <summary>
        /// Aplica dano com cálculo completo (DEF, crítico, etc.) no servidor.
        /// Sincroniza HP via SyncVar para todos e notifica o cliente dono via RPC.
        /// </summary>
        [Server]
        private void ServerProcessDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (CurrentHP <= 0f) return;

            // Recupera stats do PlayerEntity para usar DEF, FLEE, etc.
            var playerEntity = GetComponent<PlayerEntity>();
            float def  = playerEntity?.Stats?.DEF  ?? 5f;
            float mdef = playerEntity?.Stats?.MDEF ?? 5f;
            float flee = playerEntity?.Stats?.FLEE ?? 20f;
            float critDmg = playerEntity?.Stats?.CritDMG ?? 1.5f;

            bool hit = StatsCalculator.RollHit(100f, flee);
            if (!hit)
            {
                RpcShowFloating("MISS", transform.position, Color.gray);
                return;
            }

            bool  crit = StatsCalculator.RollCrit(5f);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, def, crit, critDmg)
                : StatsCalculator.CalculateMagicDamage(rawMatk, mdef, crit, critDmg);

            dmg = Mathf.Max(1f, dmg);

            // Atualiza SyncVar — propaga para todos os clientes automaticamente
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);

            // Notifica o cliente DONO para atualizar PlayerEntity e HUD local
            RpcSyncDamageToOwner(CurrentHP, MaxHP, dmg, crit, transform.position);

            Debug.Log($"[NetworkPlayer] {CharacterName} tomou {dmg:0} de dano | HP:{CurrentHP:0}/{MaxHP:0}");

            if (CurrentHP <= 0f)
                RpcOnDied();
        }

        /// <summary>
        /// Chamado pelo NetworkMonsterEntity para aplicar dano (sem cálculo extra).
        /// Usa o cálculo já feito pelo monstro.
        /// </summary>
        [Server]
        public void ServerApplyDamage(float amount)
        {
            if (CurrentHP <= 0f) return;

            CurrentHP = Mathf.Max(0f, CurrentHP - amount);

            // Notifica o dono para sincronizar PlayerEntity e HUD
            RpcSyncDamageToOwner(CurrentHP, MaxHP, amount, false, transform.position);

            Debug.Log($"[NetworkPlayer] {CharacterName} tomou {amount:0} (pré-calc) | HP:{CurrentHP:0}/{MaxHP:0}");

            if (CurrentHP <= 0f)
                RpcOnDied();
        }

        // ── ClientRpcs ────────────────────────────────────────────────────

        /// <summary>
        /// Notifica TODOS os clientes para exibir o floating text de dano.
        /// Também sincroniza o PlayerEntity local do dono.
        /// </summary>
        [ClientRpc]
        private void RpcSyncDamageToOwner(float newHP, float newMaxHP, float dmg, bool crit, Vector3 pos)
        {
            // Exibe número flutuante em todos os clientes
            Color color = crit ? Color.yellow : Color.red;
            string txt  = crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}";
            FloatingTextManager.Instance?.Show(txt, pos, color);

            // Sincroniza PlayerEntity APENAS no dono do objeto
            if (!isLocalPlayer) return;

            var playerEntity = GetComponent<PlayerEntity>();
            if (playerEntity == null) return;

            // Força o PlayerEntity a refletir o HP vindo do servidor
            playerEntity.ForceSetHP(newHP, newMaxHP);
        }

        [ClientRpc]
        public void RpcPlayAnimation(string trigger)
        {
            _animator?.SetTrigger(trigger);
        }

        [ClientRpc]
        private void RpcShowFloating(string text, Vector3 pos, Color color)
        {
            FloatingTextManager.Instance?.Show(text, pos, color);
        }

        [ClientRpc]
        private void RpcOnDied()
        {
            Debug.Log($"[NetworkPlayer] {CharacterName} morreu!");
            // Futuramente: animação de morte, respawn timer, etc.
        }

        // ── SyncVar Hooks ─────────────────────────────────────────────────

        private void OnNameChanged(string oldName, string newName)
        {
            if (nameTagText != null) nameTagText.text = newName;
        }

        private void OnRaceChanged(string oldRace, string newRace)
        {
            // Futuramente: trocar modelo 3D
        }

        private void OnHPChanged(float oldHP, float newHP)
        {
            // Atualiza barra de HP acima do player (visível para OUTROS jogadores)
            if (hpBarSlider != null)
            {
                hpBarSlider.maxValue = MaxHP;
                hpBarSlider.value    = newHP;
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
    }
}
