using UnityEngine;
using UnityEngine.AI;
using RPG.Data;
using RPG.Managers;
using RPG.UI;
using System;

namespace RPG.Character
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        // ── Runtime stats ─────────────────────────────────────────────────
        public CharacterData Data        { get; private set; }
        public DerivedStats  Stats       { get; private set; }
        public BuffBonuses   ActiveBuffs { get; private set; } = new BuffBonuses();

        public float CurrentHP { get; private set; }
        public float CurrentMP { get; private set; }

        // Inicializado indica que Initialize() foi chamado com dados válidos
        public bool IsInitialized => Data != null && Stats != null;

        // ── Eventos ───────────────────────────────────────────────────────
        public event Action<float, float> OnHPChanged;
        public event Action<float, float> OnMPChanged;
        public event Action<bool>         OnDeathChanged;
        public event Action               OnStatsChanged;

        // ── Componentes ───────────────────────────────────────────────────
        private NavMeshAgent _agent;
        public  NavMeshAgent Agent => _agent;

        private bool  _isDead;
        private float _regenTimer;
        private const float REGEN_INTERVAL = 5f;

        public ITargetable CurrentTarget { get; private set; }

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        private void Start()
        {
            // Modo offline (sem Mirror): inicializa direto com o personagem selecionado.
            // Modo online: o NetworkPlayer chama Initialize() depois do spawn.
            // No servidor dedicado: SelectedCharacter é null — não faz nada aqui.
            var charData = GameManager.Instance?.SelectedCharacter;
            if (charData != null && !IsInitialized)
                Initialize(charData);
        }

        private void Update()
        {
            // Não processa nada até estar totalmente inicializado
            if (!IsInitialized || _isDead) return;
            HandleRegen();
        }

        // ── Inicialização ─────────────────────────────────────────────────

        public void Initialize(CharacterData data)
        {
            if (data == null)
            {
                Debug.LogWarning("[PlayerEntity] Initialize chamado com data null — ignorado.");
                return;
            }

            Data = data;
            RefreshStats();

            CurrentHP = data.CurrentHP > 0 ? data.CurrentHP : Stats.MaxHP;
            CurrentMP = data.CurrentMP > 0 ? data.CurrentMP : Stats.MaxMP;

            if (_agent != null)
            {
                _agent.speed           = Mathf.Clamp(Stats.ASPD * 0.8f, 2f, 10f);
                _agent.stoppingDistance = 0.5f;
            }
        }

        // ── Stats ─────────────────────────────────────────────────────────

        public void RefreshStats()
        {
            if (Data == null) return;
            Stats = Data.GetDerivedStats(ActiveBuffs);
            if (_agent != null)
                _agent.speed = Mathf.Clamp(Stats.ASPD * 0.8f, 2f, 10f);
            OnStatsChanged?.Invoke();
        }

        // ── Movimento ─────────────────────────────────────────────────────

        public void MoveTo(Vector3 destination)
        {
            if (_isDead || _agent == null) return;
            _agent.SetDestination(destination);
        }

        public void StopMovement()
        {
            _agent?.ResetPath();
        }

        public bool HasReachedDestination()
        {
            if (_agent == null) return false;
            return !_agent.pathPending
                && _agent.remainingDistance <= _agent.stoppingDistance
                && (!_agent.hasPath || _agent.velocity.sqrMagnitude < 0.01f);
        }

        // ── Alvo ──────────────────────────────────────────────────────────

        public void SetTarget(ITargetable target)
        {
            CurrentTarget?.OnDeselected();
            CurrentTarget = target;
            CurrentTarget?.OnSelected();
        }

        public void ClearTarget()
        {
            CurrentTarget?.OnDeselected();
            CurrentTarget = null;
        }

        // ── Dano & Cura ───────────────────────────────────────────────────

        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (!IsInitialized || _isDead) return;

            bool crit = StatsCalculator.RollCrit(Stats.CRIT);
            bool hit  = StatsCalculator.RollHit(100f, Stats.FLEE);

            if (!hit)
            {
                FloatingTextManager.Instance?.Show("MISS", transform.position, Color.gray);
                return;
            }

            float dmg = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, Stats.DEF, crit, Stats.CritDMG)
                : StatsCalculator.CalculateMagicDamage(rawMatk, Stats.MDEF, crit, Stats.CritDMG);

            dmg *= 1f - (Stats.DamageReduction / 100f);
            dmg  = Mathf.Max(1f, dmg);

            CurrentHP = Mathf.Max(0, CurrentHP - dmg);
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);

            Color color = crit ? Color.yellow : Color.red;
            FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}", transform.position, color);

            if (CurrentHP <= 0) Die();
        }

        public void Heal(float amount)
        {
            if (!IsInitialized || _isDead) return;
            CurrentHP = Mathf.Min(Stats.MaxHP, CurrentHP + amount);
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            FloatingTextManager.Instance?.Show($"+{amount:0}", transform.position, Color.green);
        }

        public void RestoreMP(float amount)
        {
            if (!IsInitialized) return;
            CurrentMP = Mathf.Min(Stats.MaxMP, CurrentMP + amount);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        public bool SpendMP(float amount)
        {
            if (!IsInitialized || CurrentMP < amount) return false;
            CurrentMP -= amount;
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
            return true;
        }

        // ── Morte ─────────────────────────────────────────────────────────

        private void Die()
        {
            _isDead = true;
            _agent?.ResetPath();
            OnDeathChanged?.Invoke(true);
            Debug.Log($"[PlayerEntity] {Data?.CharacterName} morreu.");
        }

        public void Respawn(Vector3 position)
        {
            if (!IsInitialized) return;
            _isDead = false;
            transform.position = position;
            CurrentHP = Stats.MaxHP * 0.5f;
            CurrentMP = Stats.MaxMP * 0.5f;
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
            OnDeathChanged?.Invoke(false);
        }

        // ── Regen ─────────────────────────────────────────────────────────

        private void HandleRegen()
        {
            _regenTimer += Time.deltaTime;
            if (_regenTimer < REGEN_INTERVAL) return;
            _regenTimer = 0f;

            if (CurrentHP < Stats.MaxHP) Heal(Stats.HPRegen);
            if (CurrentMP < Stats.MaxMP)
            {
                CurrentMP = Mathf.Min(Stats.MaxMP, CurrentMP + Stats.MPRegen);
                OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
            }
        }

        // ── Save ──────────────────────────────────────────────────────────

        public void SaveToData()
        {
            if (!IsInitialized) return;
            if (GameManager.Instance?.CurrentAccount == null) return;

            Data.CurrentHP = CurrentHP;
            Data.CurrentMP = CurrentMP;
            Data.PosX      = transform.position.x;
            Data.PosY      = transform.position.y;
            Data.PosZ      = transform.position.z;

            SaveManager.Instance?.SaveCharacter(GameManager.Instance.CurrentAccount, Data);
        }

        private void OnApplicationQuit() => SaveToData();
    }
}
