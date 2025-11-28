using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ============================================================
// CLASES DE DATOS
// ============================================================

/// <summary>
/// Representa una observación/imagen capturada por un ScoutBot
/// </summary>
[System.Serializable]
public class Observacion
{
    public string plantaId;
    public string segmentoId;
    public Vector2Int posicionPlanta;
    public float timestamp;
    
    public Observacion(string plantaId, string segmentoId, Vector2Int posicion)
    {
        this.plantaId = plantaId;
        this.segmentoId = segmentoId;
        this.posicionPlanta = posicion;
        this.timestamp = Time.time;
    }
}

/// <summary>
/// Representa un batch de datos recolectados por un ScoutBot
/// </summary>
[System.Serializable]
public class ScoutBatch
{
    public string scoutId;
    public List<Observacion> observaciones;
    public int segmentosInspeccionados;
    public int segmentosBloqueados;
    public float tiempoTotal;
    
    public ScoutBatch(string scoutId)
    {
        this.scoutId = scoutId;
        this.observaciones = new List<Observacion>();
        this.segmentosInspeccionados = 0;
        this.segmentosBloqueados = 0;
        this.tiempoTotal = 0f;
    }
}

// ============================================================
// SCOUT BOT CORE - Agente Reactivo Independiente
// ============================================================

/// <summary>
/// Core del ScoutBot - Agente reactivo explorador completamente independiente
/// Implementa su propio sistema de movimiento basado en el grid
/// </summary>
public class ScoutBotCore : MonoBehaviour
{
    // ---------------- ESTADOS DEL SCOUT ----------------
    
    public enum ScoutState
    {
        Idle,
        EnMision,
        NavegandoSegmento,
        EscaneandoPlanta,
        RegresandoBase,
        TransfiriendoDatos
    }
    
    [Header("Estado")]
    public ScoutState estado = ScoutState.Idle;
    public string scoutId;
    
    [Header("Config")]
    [Tooltip("Velocidad en tiles por segundo")]
    public float stepsPerSecond = 2f;
    
    [Tooltip("Tiempo para tomar foto (postharvest)")]
    public float captureTime = 2f;
    
    [Tooltip("Tiempo de espera en base")]
    public float baseWaitTime = 2f;
    
    public float fixedYPosition = 1.71f;
    
    [Header("Debug")]
    public bool useDebugMission = false;
    
    [Tooltip("Si está vacío, buscará automáticamente segmentos cerca de TomatoPlants")]
    public List<Transform> debugSegmentTargets;
    
    [Tooltip("Si es true, genera segmentos automáticos alrededor de todas las plantas")]
    public bool autoGenerateSegments = true;
    
    [Header("Docking Station")]
    public Transform dockingStationTransform;
    
    [Header("Task Distribution")]
    [Tooltip("If true, waits for an external distributor to assign tasks")]
    public bool waitForDistributor = false;

    [Header("Animation")]
    private Animator animator;
    
    // ---------------- COMPONENTES ----------------
    
    private GridManager grid;
    private BotManager botManager;
    

    // ---------------- ANIMATION CONTROL ----------------
    private void SetAnimationState(int stateValue)
    {
        if (animator != null)
        {
            animator.SetInteger("State", stateValue);
        }
    }

    // ---------------- NAVEGACIÓN ----------------
    
    public Vector2Int CurrentGridPos { get; private set; }
    private Vector2Int startGridPos;
    private List<Vector2Int> currentPath = new List<Vector2Int>();
    private int currentPathIndex = 0;
    
    private Vector3 worldFrom;
    private Vector3 worldTo;
    private float stepProgress = 1f;
    
    // ---------------- MISIÓN ----------------
    
    private Vector2Int baseGridPos; // Ahora será la posición de la Docking Station si existe
    private List<Observacion> imagenesCapturadas = new List<Observacion>();
    private Queue<Vector2Int> segmentosPendientes = new Queue<Vector2Int>();
    private List<Vector2Int> segmentosInspeccionados = new List<Vector2Int>();
    private List<Vector2Int> segmentosBloqueados = new List<Vector2Int>();
    private bool isBusy = false; // Para detener el movimiento durante acciones
    
    // ---------------- UNITY LIFECYCLE ----------------
    
    private void Awake()
    {
        if (string.IsNullOrEmpty(scoutId))
        {
            scoutId = $"Scout_{gameObject.GetInstanceID()}";
        }
    }
    
    private void Start()
    {
        // Get animator from child GameObject
        animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning($"{name}: No Animator found in children!");
        }
        
        grid = GridManager.Instance;
        botManager = BotManager.Instance;
        
        // Posición inicial
        CurrentGridPos = grid.WorldToGrid(transform.position);
        startGridPos = CurrentGridPos;
        transform.position = grid.GridToWorld(CurrentGridPos);
        
        Vector3 pos = transform.position;
        pos.y = fixedYPosition;
        transform.position = pos;
        
        worldFrom = transform.position;
        worldTo = transform.position;
        stepProgress = 1f;

        SetAnimationState(1);
        
        // Registrar en BotManager (necesita un BotController, usamos null por ahora)
        // botManager.RegisterBot(this, CurrentGridPos);
        
        // La "Base" principal será la Docking Station si existe, sino la posición inicial
        if (dockingStationTransform != null)
        {
            baseGridPos = grid.WorldToGrid(dockingStationTransform.position);
        }
        else
        {
            baseGridPos = startGridPos;
        }
        
        estado = ScoutState.Idle;
        
        // Debug
        Debug.Log($"{name}: Start completed. useDebugMission = {useDebugMission}");
        
        if (waitForDistributor)
        {
            Debug.Log($"{name}: Waiting for Task Distributor...");
        }
        else if (useDebugMission)
        {
            Debug.Log($"{name}: Starting debug mission coroutine");
            StartCoroutine(IniciarMisionDebugCoroutine());
        }
        else
        {
            Debug.Log($"{name}: useDebugMission is disabled, waiting for manual mission");
        }
    }
    
    private IEnumerator IniciarMisionDebugCoroutine()
    {
        yield return new WaitForSeconds(1f);
        Debug.Log($"{name}: Attempting to start debug mission...");
        IniciarMisionDebug();
    }
    
    private void Update()
    {
        // Si está ocupado (escaneando, esperando, etc), no moverse
        if (isBusy)
            SetAnimationState(0);
            return;
        
        // Animación entre tiles
        if (stepProgress < 1f)
        {
            float delta = Time.deltaTime * stepsPerSecond;
            stepProgress += delta;
            float t = Mathf.Clamp01(stepProgress);
            transform.position = Vector3.Lerp(worldFrom, worldTo, t);
            
            Vector3 pos = transform.position;
            pos.y = fixedYPosition;
            transform.position = pos;
            
            return;
        }
        
        MoveAlongPath();
    }
    
    // ---------------- MOVIMIENTO ----------------
    
    private void MoveAlongPath()
    {
        if (currentPath == null || currentPath.Count == 0) return;
        
        if (currentPathIndex >= currentPath.Count)
        {
            currentPath.Clear();
            currentPathIndex = 0;
            return;
        }
        
        Vector2Int targetGridPos = currentPath[currentPathIndex];
        
        // Verificar si el tile está libre
        if (!grid.IsWalkable(targetGridPos))
        {
            return; // Esperar
        }
        
        SetAnimationState(1);
        worldFrom = grid.GridToWorld(CurrentGridPos);
        CurrentGridPos = targetGridPos;
        worldTo = grid.GridToWorld(CurrentGridPos);
        
        // Rotación
        Vector3 direction = worldTo - worldFrom;
        if (direction.sqrMagnitude > 0.01f)
        {
            direction.y = 0;
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            transform.rotation = targetRotation;
        }
        
        worldFrom.y = fixedYPosition;
        worldTo.y = fixedYPosition;
        
        stepProgress = 0f;
        currentPathIndex++;
    }
    
    private void SetTarget(Vector2Int targetGridPos)
    {
        currentPath = grid.FindPath(CurrentGridPos, targetGridPos, null);
        currentPathIndex = 0;
        
        if (currentPath == null || currentPath.Count == 0)
        {
            Debug.LogWarning($"{name}: No path found from {CurrentGridPos} to {targetGridPos}");
        }
        else
        {
            Debug.Log($"{name}: Path calculated to {targetGridPos} ({currentPath.Count} steps)");
        }
    }
    
    private void IniciarMisionDebug()
    {
        List<Vector2Int> segmentos = new List<Vector2Int>();
        
        // Si hay targets manuales, usarlos
        if (debugSegmentTargets != null && debugSegmentTargets.Count > 0)
        {
            foreach (var target in debugSegmentTargets)
            {
                if (target != null)
                {
                    Vector2Int gridPos = grid.WorldToGrid(target.position);
                    segmentos.Add(gridPos);
                    Debug.Log($"{name}: Added manual segment {gridPos} from {target.name}");
                }
            }
        }
        // Si no hay targets y autoGenerate está activo, buscar plantas
        else if (autoGenerateSegments)
        {
            Debug.Log($"{name}: Automatically generating segments around plants...");
            segmentos = GenerarSegmentosDesdesPlantas();
        }
        else
        {
            Debug.LogWarning($"{name}: No debugSegmentTargets and autoGenerateSegments is disabled!");
            return;
        }
        
        if (segmentos.Count > 0)
        {
            Debug.Log($"{name}: Starting mission with {segmentos.Count} segments");
            AsignarMision(segmentos);
        }
        else
        {
            Debug.LogWarning($"{name}: No valid segments generated");
        }
    }
    
    private List<Vector2Int> GenerarSegmentosDesdesPlantas()
    {
        List<Vector2Int> segmentos = new List<Vector2Int>();
        
        Debug.Log($"{name}: Searching for harvest spots...");
        Debug.Log($"{name}: Current position: {CurrentGridPos}");
        
        // Buscar todos los tiles con isHarvestSpot = true
        TileInfo[] todosTiles = FindObjectsOfType<TileInfo>();
        
        // Filtrar tiles válidos
        System.Collections.Generic.List<(Vector2Int pos, float distancia)> candidatos = 
            new System.Collections.Generic.List<(Vector2Int, float)>();
        
        foreach (var tile in todosTiles)
        {
            if (tile.isHarvestSpot)
            {
                // Verificar que el tile sea caminable
                if (!tile.walkable)
                {
                    continue;
                }

                float dist = Vector2Int.Distance(CurrentGridPos, tile.gridPos);
                candidatos.Add((tile.gridPos, dist));
            }
        }
        
        Debug.Log($"{name}: Found {candidatos.Count} harvest spots.");
        
        // Extract points from candidates
        List<Vector2Int> rawPoints = new List<Vector2Int>();
        foreach(var c in candidatos) rawPoints.Add(c.pos);
        
        // Optimize using Snake Sort
        segmentos = OptimizePath(rawPoints);

        Debug.Log($"{name}: Optimized path (Vertical Snake) generated with {segmentos.Count} points.");
        
        Debug.Log($"{name}: Total: {segmentos.Count} harvest spots.");
        
        if (segmentos.Count == 0)
        {
            Debug.LogError($"{name}: No tiles with isHarvestSpot = true found!");
        }
        
        return segmentos;
    }
    
    /// <summary>
    /// Optimizes a list of points using Vertical Snake Sort
    /// </summary>
    public List<Vector2Int> OptimizePath(List<Vector2Int> points)
    {
        List<Vector2Int> optimized = new List<Vector2Int>();
        
        // Group by columns (X)
        SortedDictionary<int, List<Vector2Int>> groupsByX = new SortedDictionary<int, List<Vector2Int>>();
        
        foreach(var p in points)
        {
            if(!groupsByX.ContainsKey(p.x))
                groupsByX[p.x] = new List<Vector2Int>();
            groupsByX[p.x].Add(p);
        }
        
        int colIndex = 0;
        foreach(var kvp in groupsByX)
        {
            var colPoints = kvp.Value;
            // Sort by Y (rows)
            colPoints.Sort((a,b) => a.y.CompareTo(b.y));
            
            // Alternate direction every column
            if (colIndex % 2 == 1)
                colPoints.Reverse();
                
            optimized.AddRange(colPoints);
            colIndex++;
        }
        
        return optimized;
    }
    
    // ---------------- API MISIÓN ----------------
    
    public void AsignarMision(List<Vector2Int> segmentos, bool optimize = false)
    {
        if (estado != ScoutState.Idle)
        {
            Debug.LogWarning($"{name}: Cannot receive mission in state {estado}");
            return;
        }
        
        segmentosPendientes.Clear();
        segmentosInspeccionados.Clear();
        segmentosBloqueados.Clear();
        imagenesCapturadas.Clear();
        
        List<Vector2Int> finalSegments = optimize ? OptimizePath(segmentos) : segmentos;
        
        foreach (var seg in finalSegments)
        {
            segmentosPendientes.Enqueue(seg);
        }
        
        estado = ScoutState.EnMision;
        StartCoroutine(EjecutarMisionCompleta());
    }
    

    
    // ---------------- PERCEPCIÓN ----------------
    
    private List<Vector2Int> DetectarPlantas(Vector2Int segmentoPos)
    {
        List<Vector2Int> plantas = new List<Vector2Int>();
        
        Vector2Int[] dirs = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
        
        foreach (var dir in dirs)
        {
            Vector2Int neighborPos = segmentoPos + dir;
            TileInfo tile = grid.GetTile(neighborPos);
            
            if (tile != null && !tile.walkable && tile.hasPlant)
            {
                plantas.Add(neighborPos);
            }
        }
        
        return plantas;
    }
    
    private Observacion CapturarImagen(Vector2Int plantaPos, string segmentoId)
    {
        string plantaId = $"Planta_{plantaPos.x}_{plantaPos.y}";
        return new Observacion(plantaId, segmentoId, plantaPos);
    }
    
    // ---------------- EJECUCIÓN DE MISIÓN ----------------
    
    private IEnumerator EjecutarMisionCompleta()
    {
        float tiempoInicio = Time.time;
        
        // 1. IR A LA DOCKING STATION (INICIO)
        Debug.Log($"{name}: 1. Starting sequence - Going to Docking Station...");
        
        // Usar la nueva rutina de cola física
        yield return StartCoroutine(IrYUsarDockingStation());
        
        // 2. VISITAR SEGMENTOS (PATRULLAJE)
        Debug.Log($"{name}: 2. Starting photo patrol...");
        
        while (segmentosPendientes.Count > 0)
        {
            Vector2Int segmento = segmentosPendientes.Dequeue();
            
            Debug.Log($"{name}: Navigating to segment {segmento} (Y={segmento.y}, X={segmento.x}) from {CurrentGridPos} - {segmentosPendientes.Count} remaining.");
            estado = ScoutState.NavegandoSegmento;
            isBusy = false; // Asegurar que puede moverse
            SetTarget(segmento);
            
            float timeout = 30f;
            float elapsed = 0f;
            Vector2Int lastPos = CurrentGridPos;
            float stuckTimer = 0f;
            
            while (CurrentGridPos != segmento && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                
                // Detectar si está atascado
                if (CurrentGridPos == lastPos)
                {
                    stuckTimer += Time.deltaTime;
                    if (stuckTimer > 5f) // Atascado por 5 segundos
                    {
                        Debug.LogWarning($"{name}: Stuck at {CurrentGridPos}, aborting segment {segmento}");
                        break;
                    }
                }
                else
                {
                    lastPos = CurrentGridPos;
                    stuckTimer = 0f;
                }
                
                yield return null;
            }
            
            if (CurrentGridPos != segmento)
            {
                segmentosBloqueados.Add(segmento);
                Debug.LogWarning($"{name}: Did not reach {segmento}, skipping...");
                continue;
            }
            
            // LLEGAMOS AL SEGMENTO - DETENER COMPLETAMENTE
            Debug.Log($"{name}: Reached segment {segmento} - STOPPING MOVEMENT");
            isBusy = true;
            currentPath.Clear(); // Limpiar ruta para asegurar que no se mueva
            estado = ScoutState.EscaneandoPlanta;
            List<Vector2Int> plantas = DetectarPlantas(segmento);
            string segmentoId = $"Seg_{segmento.x}_{segmento.y}";
            
            if (plantas.Count > 0)
            {
                Debug.Log($"{name}: STOPPED - Scanning {plantas.Count} plants in {segmentoId}...");
                
                foreach (var planta in plantas)
                {
                    Debug.Log($"{name}: PHOTOGRAPHING plant at {planta} - WAITING {captureTime} seconds...");
                    yield return new WaitForSeconds(captureTime);
                    Observacion obs = CapturarImagen(planta, segmentoId);
                    imagenesCapturadas.Add(obs);
                    Debug.Log($"{name}: Photo captured!");
                }
                
                Debug.Log($"{name}: Scan complete - {plantas.Count} plants photographed");
            }
            else
            {
                Debug.Log($"{name}: No plants around segment {segmentoId}");
            }
            
            segmentosInspeccionados.Add(segmento);
            isBusy = false; // Reanudar movimiento
        }
        
        // 3. REGRESAR A DOCKING STATION (FIN DE FOTOS)
        Debug.Log($"{name}: 3. Photos finished. Going to Docking Station...");
        
        // Usar la nueva rutina de cola física
        yield return StartCoroutine(IrYUsarDockingStation());
        
        // 4. REGRESAR A POSICIÓN INICIAL
        Debug.Log($"{name}: 4. Returning home {startGridPos}");
        estado = ScoutState.RegresandoBase;
        
        List<Vector2Int> pathToHome = grid.FindPath(CurrentGridPos, startGridPos, null);
        if (pathToHome != null && pathToHome.Count > 0)
        {
            SetTarget(startGridPos);
            
            float timeoutHome = 60f;
            float elapsedHome = 0f;
            while (CurrentGridPos != startGridPos && elapsedHome < timeoutHome)
            {
                elapsedHome += Time.deltaTime;
                yield return null;
            }
            
            if (CurrentGridPos == startGridPos)
            {
                Debug.Log($"{name}: Reached home.");
            }
        }
        else
        {
             Debug.LogWarning($"{name}: Home unreachable.");
        }

        // 5. TRANSFERIR DATOS (Simulado al final)
        isBusy = true;
        estado = ScoutState.TransfiriendoDatos;
        Debug.Log($"{name}: STOPPED - Transferring data... (waiting {baseWaitTime}s)");
        yield return new WaitForSeconds(baseWaitTime);
        isBusy = false;
        
        float tiempoTotal = Time.time - tiempoInicio;
        Debug.Log($"{name}: SEQUENCE COMPLETED");
        Debug.Log($"{name}: - Observations: {imagenesCapturadas.Count}");
        Debug.Log($"{name}: - Inspected segments: {segmentosInspeccionados.Count}");
        Debug.Log($"{name}: - Blocked segments: {segmentosBloqueados.Count}");
        Debug.Log($"{name}: - Total time: {tiempoTotal:F2}s");
        
        imagenesCapturadas.Clear();
        estado = ScoutState.Idle;
    }
    
    // ---------------- GIZMOS ----------------
    
    private void OnDrawGizmosSelected()
    {
        if (grid == null) return;
        
        // Ruta actual
        if (currentPath != null && currentPath.Count > 0)
        {
            Gizmos.color = Color.cyan;
            for (int i = currentPathIndex; i < currentPath.Count; i++)
            {
                Vector3 p = grid.GridToWorld(currentPath[i]) + Vector3.up * 0.05f;
                Gizmos.DrawSphere(p, 0.15f);
            }
        }
        
        // Base
        /*
        if (patologoTransform != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(patologoTransform.position, 0.5f);
        }
        */
    }
    
    // ---------------- GESTIÓN DE COLA FÍSICA (DOCKING) ----------------
    
    // Gestión estática de la cola para Scouts (independiente de BotManager pero usando sus slots)
    private static ScoutBotCore currentStationUser = null;
    private static List<ScoutBotCore> scoutQueue = new List<ScoutBotCore>();

    private IEnumerator IrYUsarDockingStation()
    {
        // 1. Registrarse en la cola si no estoy y no soy el usuario actual
        if (!scoutQueue.Contains(this) && currentStationUser != this)
        {
            scoutQueue.Add(this);
        }

        bool enEstacion = false;
        Vector2Int? lastTarget = null;

        // Bucle de aproximación y espera en cola
        while (!enEstacion)
        {
            // Caso A: Puedo entrar a la estación
            // Si nadie la usa Y (soy el primero en la cola O la cola soy yo)
            if (currentStationUser == null && (scoutQueue.Count > 0 && scoutQueue[0] == this))
            {
                // Reclamar estación
                currentStationUser = this;
                if (scoutQueue.Contains(this)) scoutQueue.Remove(this);
                
                // Moverse a la estación
                Debug.Log($"{name}: Turn obtained. Going to station {baseGridPos}");
                estado = ScoutState.RegresandoBase;
                SetTarget(baseGridPos);
                
                // Esperar a llegar
                while (CurrentGridPos != baseGridPos) yield return null;
                enEstacion = true;
            }
            // Caso B: Debo esperar en la cola
            else
            {
                // Averiguar mi índice en la cola
                int myIndex = scoutQueue.IndexOf(this);
                
                if (myIndex != -1)
                {
                    // Obtener posición física del slot desde BotManager
                    var slots = BotManager.Instance.dockingQueueSlots;
                    if (slots != null && myIndex < slots.Count)
                    {
                        Vector2Int slotPos = grid.WorldToGrid(slots[myIndex].position);
                        
                        // Si mi destino actual no es este slot, moverme ahí
                        // Verificamos si ya estamos yendo ahí para no recalcular ruta a cada frame
                        bool atSlot = (CurrentGridPos == slotPos);
                        bool movingToSlot = (lastTarget.HasValue && lastTarget.Value == slotPos && currentPath.Count > 0);

                        if (!atSlot && !movingToSlot)
                        {
                             Debug.Log($"{name}: Waiting in queue (Pos {myIndex}). Going to slot {slotPos}");
                             estado = ScoutState.RegresandoBase;
                             SetTarget(slotPos);
                             lastTarget = slotPos;
                        }
                    }
                    else
                    {
                        // No hay slots definidos o estoy muy atrás, esperar donde estoy
                        if (currentPath != null) currentPath.Clear();
                    }
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        // 2. Usar estación (Carga)
        Debug.Log($"{name}: Docked at station...");
        // estado = ScoutState.Recargando; // Removed battery logic
        yield return new WaitForSeconds(2f); // Simular espera
        // batteryLevel = maxBattery; // Removed battery logic
        
        // 3. Liberar estación
        if (currentStationUser == this) currentStationUser = null;
        Debug.Log($"{name}: Docking complete. Releasing station.");
    }
}
