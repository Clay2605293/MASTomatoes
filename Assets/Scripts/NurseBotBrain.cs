using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// NurseBot: toma muestras de plantas sospechosas y las lleva a ENFPos para análisis.
/// Flujo:
/// 1. Cuando el Orchestrator lo indica, va a DSPos inicial (espera turno).
/// 2. Recibe lista de TomatoTask sospechosas (AssignTasks).
/// 3. Para cada tarea:
///    - Va a standPos de la planta.
///    - Toma muestra (ConsumeTomato).
///    - Va a ENFPos y analiza (usa isTrulySick), usando una cola física compartida.
/// 4. Cuando termina todas, vuelve a DSPos, reporta y regresa a home.
/// </summary>
public class NurseBotBrain : BaseGridBot
{
    [Header("Stations")]
    public Transform dsTransform;     // DSPos (misma docking que los demás)
    public Transform enfTransform;    // ENFPos (estación de enfermería / laboratorio)



    [Header("Timings")]
    [Tooltip("Tiempo que tarda en tomar una muestra en la planta.")]
    public float sampleTime = 0.5f;

    [Tooltip("Tiempo que tarda analizando una muestra en ENFPos.")]
    public float analysisTime = 1.0f;

    [Header("Movement / Visual")]
    public float stepsPerSecond = 2f;
    public float fixedY = 1.71f;

    [Header("Animation")]
    private Animator animator;

    [Header("Mission State")]
    public bool MissionComplete { get; private set; }

    // Flag para no arrancar hasta que el Orchestrator lo indique
    private bool missionStarted = false;

    // Referencias
    private GridManager grid;
    private BotManager botManager;

    // Posiciones clave (grid)
    private Vector2Int dsGridPos;
    private Vector2Int enfGridPos;
    private Vector2Int homeGridPos;

    // Lista de tareas asignadas (plantas sospechosas)
    private List<TomatoFieldManager.TomatoTask> assignedTasks = new();
    private int currentTaskIndex = 0;
    private TomatoFieldManager.TomatoTask currentTask = null;

    // Path actual dentro del grid
    private List<Vector2Int> currentPath = new();
    private int currentPathIndex = 0;

    // Interpolación entre tiles
    private Vector3 worldFrom;
    private Vector3 worldTo;
    private float stepProgress = 1f; // 1 = ya está en el tile destino

    private bool forceReplan = false;
    private bool busy = false;

    // Estado interno del NurseBot
    private enum NurseState
    {
        GoingToDS_Initial,
        WaitingAtDS_Initial,
        GoingToPlant,
        SamplingAtPlant,
        GoingToENF,
        WaitingAtENF,
        GoingToDS_Final,
        WaitingAtDS_Final,
        ReturningHome,
        Idle
    }

    private NurseState state = NurseState.Idle;

    // ----------------- ENF: cola física propia (usa dockingQueueSlots) -----------------

    private static NurseBotBrain enfOwner = null;
    private static Queue<NurseBotBrain> enfQueueOrder = new Queue<NurseBotBrain>();
    private static Dictionary<NurseBotBrain, int> enfQueueIndex = new Dictionary<NurseBotBrain, int>();
    private static Vector2Int[] enfQueueGridPos;
    private static bool enfQueueBuilt = false;

    private void EnsureEnfQueueGridPos()
    {
        if (enfQueueBuilt) return;

        var gridMgr = GridManager.Instance;
        var manager = BotManager.Instance;

        if (gridMgr == null || manager == null || manager.dockingQueueSlots == null || manager.dockingQueueSlots.Count == 0)
        {
            enfQueueGridPos = new Vector2Int[0];
            enfQueueBuilt = true;
            return;
        }

        enfQueueGridPos = new Vector2Int[manager.dockingQueueSlots.Count];
        for (int i = 0; i < manager.dockingQueueSlots.Count; i++)
        {
            enfQueueGridPos[i] = gridMgr.WorldToGrid(manager.dockingQueueSlots[i].position);
        }

        enfQueueBuilt = true;
    }

    // ----------------- BaseGridBot Required -----------------

    public override float RemainingCost
    {
        get
        {
            int tasksRemaining = (assignedTasks != null ? assignedTasks.Count - currentTaskIndex : 0);
            int pathRemaining = (currentPath != null ? currentPath.Count - currentPathIndex : 0);
            return tasksRemaining * 10 + pathRemaining;
        }
    }

    public override void ForceReplan()
    {
        forceReplan = true;
    }

    // ----------------- UNITY -----------------

    private void Start()
    {
        MissionComplete = false;
        missionStarted = false;

        grid = GridManager.Instance;
        botManager = BotManager.Instance;

        animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning($"{name}: NurseBotBrain no encontró Animator en hijos.");
        }

        // Posición inicial en el mundo -> grid
        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);
        FixY();

        // Get home tile from parent's PositionToHomeTile component
        Transform homeTransform = GetHomeTileFromParent();
        
        // Home: si hay homeTransform, usarlo; si no, usar posición inicial
        if (homeTransform != null)
        {
            homeGridPos = grid.WorldToGrid(homeTransform.position);
            
            // Align bot's X and Z position to home tile
            Vector3 alignedPosition = transform.position;
            alignedPosition.x = homeTransform.position.x;
            alignedPosition.z = homeTransform.position.z;
            transform.position = alignedPosition;
        }
        else
        {
            homeGridPos = CurrentGridPos;
        }

        // Posición de la Docking Station (DSPos)
        if (dsTransform != null)
        {
            dsGridPos = grid.WorldToGrid(dsTransform.position);
        }
        else
        {
            Debug.LogWarning($"{name}: dsTransform no asignado, usando posición inicial como DS/home.");
            dsGridPos = homeGridPos;
        }

        // Posición de ENFPos (laboratorio)
        if (enfTransform != null)
        {
            enfGridPos = grid.WorldToGrid(enfTransform.position);
        }
        else
        {
            Debug.LogWarning($"{name}: enfTransform no asignado. Usando DS como estación de análisis.");
            enfGridPos = dsGridPos;
        }

        // Seguridad: si el home inicial no es walkable, usamos DS como home
        if (!grid.IsWalkable(homeGridPos))
        {
            Debug.LogWarning($"{name}: homeGridPos {homeGridPos} no es walkable. Usando DS como home.");
            homeGridPos = dsGridPos;
        }

        // Registrar en BotManager para colisiones
        botManager.RegisterBot(this, CurrentGridPos);

        // Inicializar interpolación
        worldFrom = transform.position;
        worldTo = transform.position;
        stepProgress = 1f;

        // Arranca en Idle hasta que el Orchestrator llame StartMission()
        state = NurseState.Idle;
        SetAnimationState(0);
    }

    /// <summary>
    /// Gets the home tile Transform from the parent's PositionToHomeTile component
    /// </summary>
    private Transform GetHomeTileFromParent()
    {
        if (transform.parent == null)
        {
            Debug.LogWarning($"{name}: No parent object found. Cannot inherit home tile.");
            return null;
        }

        PositionToHomeTile positionScript = transform.parent.GetComponent<PositionToHomeTile>();
        if (positionScript == null)
        {
            Debug.LogWarning($"{name}: Parent object does not have PositionToHomeTile component. Cannot inherit home tile.");
            return null;
        }

        if (positionScript.homeTile == null)
        {
            Debug.LogWarning($"{name}: Parent's PositionToHomeTile.homeTile is not assigned!");
            return null;
        }

        return positionScript.homeTile.transform;
    }


    private void Update()
    {
        // Hasta que el Orchestrator no arranque la misión, el NurseBot no se mueve
        if (!missionStarted) return;

        if (busy) return;

        // 1) Si estamos a medio paso entre tiles, seguir interpolando
        if (stepProgress < 1f)
        {
            float delta = Time.deltaTime * stepsPerSecond;
            stepProgress += delta;
            float t = Mathf.Clamp01(stepProgress);
            transform.position = Vector3.Lerp(worldFrom, worldTo, t);
            FixY();
            return;
        }

        // 2) Si no estamos a medio paso, avanzar en el path (si hay)
        MoveAlongPath();

        // 3) Estados que requieren reintentar reclamar DS o ENF
        switch (state)
        {
            case NurseState.WaitingAtDS_Initial:
                TryGoToDSInitial();
                break;

            case NurseState.WaitingAtDS_Final:
                TryGoToDSFinal();
                break;

            case NurseState.WaitingAtENF:
                TryGoToENF();
                break;
        }
    }

    // ----------------- API pública -----------------

    /// <summary>
    /// Orchestrator llama esto para asignar las tareas sospechosas.
    /// </summary>
    public void AssignTasks(List<TomatoFieldManager.TomatoTask> tasks)
    {
        assignedTasks = tasks ?? new List<TomatoFieldManager.TomatoTask>();
        currentTaskIndex = 0;
        currentTask = null;
    }

    /// <summary>
    /// Orchestrator llama esto cuando ya terminó el patólogo
    /// y quiere que los NurseBots empiecen su misión.
    /// </summary>
    public void StartMission()
    {
        if (missionStarted) return;

        missionStarted = true;
        state = NurseState.GoingToDS_Initial;
        SetAnimationState(1);
        TryGoToDSInitial();
    }

    // ----------------- Movimiento entre tiles -----------------

    private void MoveAlongPath()
    {
        if (currentPath == null || currentPath.Count == 0)
            return;

        if (forceReplan && currentPath.Count > 0)
        {
            Vector2Int finalTarget = currentPath[currentPath.Count - 1];
            currentPath = grid.FindPath(CurrentGridPos, finalTarget, this);
            currentPathIndex = 0;
            forceReplan = false;
        }

        if (currentPathIndex >= currentPath.Count)
        {
            currentPath.Clear();
            currentPathIndex = 0;
            OnArrivedToTile();
            return;
        }

        Vector2Int next = currentPath[currentPathIndex];

        bool canMove = botManager.TryMoveWithPriority(this, CurrentGridPos, next);
        if (!canMove) return;

        worldFrom = grid.GridToWorld(CurrentGridPos);
        CurrentGridPos = next;
        worldTo = grid.GridToWorld(CurrentGridPos);

        // Giro hacia la dirección del movimiento
        Vector3 dir = worldTo - worldFrom;
        if (dir.sqrMagnitude > 0.01f)
        {
            dir.y = 0;
            Quaternion targetRot = Quaternion.LookRotation(dir);
            transform.rotation = targetRot;
        }

        worldFrom.y = fixedY;
        worldTo.y = fixedY;

        stepProgress = 0f;
        currentPathIndex++;

        SetAnimationState(1);
    }

    private void FixY()
    {
        var p = transform.position;
        p.y = fixedY;
        transform.position = p;
    }

    private void SetTarget(Vector2Int target)
    {
        // Ya estoy en el tile destino
        if (target == CurrentGridPos)
        {
            currentPath.Clear();
            currentPathIndex = 0;
            OnArrivedToTile();
            return;
        }

        currentPath = grid.FindPath(CurrentGridPos, target, this);
        currentPathIndex = 0;

        if (currentPath == null || currentPath.Count == 0)
        {
            Debug.LogWarning($"{name}: no hay camino a {target}. Estado actual: {state}");

            // Salvavidas: si estoy en ReturningHome y no hay camino, no me quedo colgado
            if (state == NurseState.ReturningHome)
            {
                MissionComplete = true;
                state = NurseState.Idle;
                SetAnimationState(0);
                Debug.LogWarning($"{name}: no pude llegar a home, marco MissionComplete igualmente.");
            }

            return;
        }
    }

    // ----------------- Lógica de estados -----------------

    private void OnArrivedToTile()
    {
        switch (state)
        {
            case NurseState.GoingToDS_Initial:
                if (CurrentGridPos == dsGridPos)
                    StartCoroutine(WaitAtDSInitial());
                break;

            case NurseState.GoingToPlant:
                StartCoroutine(SampleAtPlant());
                break;

            case NurseState.GoingToENF:
                if (CurrentGridPos == enfGridPos)
                    StartCoroutine(WaitAtENF());
                break;

            case NurseState.GoingToDS_Final:
                if (CurrentGridPos == dsGridPos)
                    StartCoroutine(WaitAtDSFinal());
                break;

            case NurseState.ReturningHome:
                state = NurseState.Idle;
                SetAnimationState(0);
                MissionComplete = true;
                Debug.Log($"[Nurse] {name} completó su misión y regresó a home.");
                break;
        }
    }

    // --- FASE 1: IR / HACER FILA EN DS INICIAL (usa BotManager, igual que antes) ---

    private void TryGoToDSInitial()
    {
        if (botManager.TryClaimDocking(this))
        {
            botManager.ReleaseDockingQueueSlot(this);
            state = NurseState.GoingToDS_Initial;
            SetTarget(dsGridPos);
            return;
        }

        if (botManager.TryGetDockingQueueSlot(this, out var queuePos))
        {
            if (CurrentGridPos != queuePos)
            {
                SetTarget(queuePos);
            }
        }

        if (state == NurseState.GoingToDS_Initial)
            state = NurseState.WaitingAtDS_Initial;
    }

    private IEnumerator WaitAtDSInitial()
    {
        busy = true;
        state = NurseState.WaitingAtDS_Initial;
        SetAnimationState(0);

        // Briefing con el patólogo
        yield return new WaitForSeconds(1.0f);

        busy = false;
        botManager.ReleaseDocking(this);

        // Empieza la fase de muestreo
        GoToNextTask();
    }

    // --- FASE 2: PROCESAR PLANTAS SOSPECHOSAS ---

    private void GoToNextTask()
    {
        if (assignedTasks == null || assignedTasks.Count == 0)
        {
            // No hay tareas -> vamos directo a DS final
            state = NurseState.GoingToDS_Final;
            TryGoToDSFinal();
            return;
        }

        if (currentTaskIndex >= assignedTasks.Count)
        {
            // Ya no hay más tareas -> DS final
            state = NurseState.GoingToDS_Final;
            TryGoToDSFinal();
            return;
        }

        currentTask = assignedTasks[currentTaskIndex];

        // Ir al standPos de la planta sospechosa
        state = NurseState.GoingToPlant;
        SetTarget(currentTask.standPos);
        SetAnimationState(1);
    }

    private IEnumerator SampleAtPlant()
    {
        if (currentTask == null)
        {
            GoToNextTask();
            yield break;
        }

        busy = true;
        state = NurseState.SamplingAtPlant;
        SetAnimationState(0);

        // Tiempo de "tomar muestra"
        yield return new WaitForSeconds(sampleTime);

        // Consumir 1 tomate como muestra (visual + verdad médica)
        bool sampleWasSick = TomatoFieldManager.Instance.ConsumeTomato(currentTask);

        Debug.Log(
            $"[Nurse] {name} tomó muestra de planta en {currentTask.plantPos}. " +
            $"appearsSuspicious={currentTask.appearsSuspicious}, " +
            $"isTrulySick={currentTask.isTrulySick}, " +
            $"sampleWasSick={sampleWasSick}."
        );

        busy = false;

        // Llevar la muestra a ENFPos para análisis (ahora con cola física)
        TryGoToENF();
    }

    // --- FASE 2b: COLA FÍSICA EN ENFPos (usa dockingQueueSlots) ---

    private void TryGoToENF()
    {
        EnsureEnfQueueGridPos();

        // 1) Si ENF está libre o ya soy el dueño, intento ir directo
        if (enfOwner == null || enfOwner == this)
        {
            // Si hay fila, solo el primero puede entrar
            if (enfOwner == null && enfQueueOrder.Count > 0 && enfQueueOrder.Peek() != this)
            {
                // no soy el primero -> me quedo en la cola
            }
            else
            {
                // Reclamo ENF
                enfOwner = this;

                // Si estaba en la cola, salir de ella
                if (enfQueueOrder.Count > 0 && enfQueueOrder.Peek() == this)
                {
                    enfQueueOrder.Dequeue();
                }
                enfQueueIndex.Remove(this);

                // Ir directo a ENFPos
                state = NurseState.GoingToENF;
                SetTarget(enfGridPos);
                SetAnimationState(1);
                return;
            }
        }

        // 2) ENF ocupado por otro -> obtener/usar slot de fila física
        if (enfQueueGridPos == null || enfQueueGridPos.Length == 0)
        {
            // No hay slots definidos -> simplemente esperar donde esté
            state = NurseState.WaitingAtENF;
            return;
        }

        // Asignar slot si no tiene
        if (!enfQueueIndex.TryGetValue(this, out int slotIndex))
        {
            // buscar primer slot libre
            for (int i = 0; i < enfQueueGridPos.Length; i++)
            {
                bool used = false;
                foreach (var kv in enfQueueIndex)
                {
                    if (kv.Value == i)
                    {
                        used = true;
                        break;
                    }
                }

                if (!used)
                {
                    enfQueueIndex[this] = i;
                    enfQueueOrder.Enqueue(this);
                    slotIndex = i;
                    break;
                }
            }
        }

        if (enfQueueIndex.TryGetValue(this, out slotIndex))
        {
            Vector2Int slotPos = enfQueueGridPos[slotIndex];
            if (CurrentGridPos != slotPos)
            {
                state = NurseState.WaitingAtENF;
                SetTarget(slotPos);
                SetAnimationState(1);
            }
            else
            {
                state = NurseState.WaitingAtENF;
                SetAnimationState(0);
            }
        }
        else
        {
            // No encontró slot libre, solo quedarse quieto
            state = NurseState.WaitingAtENF;
        }
    }

    private IEnumerator WaitAtENF()
    {
        busy = true;
        state = NurseState.WaitingAtENF;
        SetAnimationState(0);

        // Tiempo de análisis en laboratorio
        yield return new WaitForSeconds(analysisTime);

        // Reporte de resultado (usamos la verdad de la planta)
        if (currentTask != null)
        {
            Debug.Log(
                $"[Nurse] {name} analizó muestra de planta en {currentTask.plantPos}. " +
                $"RESULTADO REAL: {(currentTask.isTrulySick ? "ENFERMA" : "SANA")}."
            );
        }

        busy = false;

        // Liberar ENF para el siguiente de la cola
        if (enfOwner == this)
        {
            enfOwner = null;
        }

        // Siguiente planta
        currentTaskIndex++;
        GoToNextTask();
    }

    // --- FASE 3: DS FINAL Y REGRESO A CASA ---

    private void TryGoToDSFinal()
    {
        if (botManager.TryClaimDocking(this))
        {
            botManager.ReleaseDockingQueueSlot(this);
            state = NurseState.GoingToDS_Final;
            SetTarget(dsGridPos);
            return;
        }

        if (botManager.TryGetDockingQueueSlot(this, out var queuePos))
        {
            if (CurrentGridPos != queuePos)
            {
                SetTarget(queuePos);
            }
        }

        if (state == NurseState.GoingToDS_Final)
            state = NurseState.WaitingAtDS_Final;
    }

    private IEnumerator WaitAtDSFinal()
    {
        busy = true;
        state = NurseState.WaitingAtDS_Final;
        SetAnimationState(0);

        // Reporte final al patólogo
        yield return new WaitForSeconds(1.0f);

        busy = false;
        botManager.ReleaseDocking(this);

        // Regresar a home
        state = NurseState.ReturningHome;
        SetTarget(homeGridPos);
        SetAnimationState(1);
    }

    // ----------------- Animación -----------------

    private void SetAnimationState(int stateValue)
    {
        if (animator == null) return;
        animator.SetInteger("State", stateValue);
    }
}
