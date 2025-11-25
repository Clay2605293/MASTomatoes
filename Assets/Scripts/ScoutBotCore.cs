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
    
    [Header("Zona de Patrullaje")]
    [Tooltip("Top = mitad superior, Bottom = mitad inferior")]
    public ZonaPatrullaje zonaAsignada = ZonaPatrullaje.Top;
    
    public enum ZonaPatrullaje
    {
        Top,    // Mitad superior del mapa
        Bottom  // Mitad inferior del mapa
    }
    
    [Header("Base/Patólogo")]
    public Transform patologoTransform;
    
    // ---------------- COMPONENTES ----------------
    
    private GridManager grid;
    private BotManager botManager;
    
    // ---------------- NAVEGACIÓN ----------------
    
    public Vector2Int CurrentGridPos { get; private set; }
    private List<Vector2Int> currentPath = new List<Vector2Int>();
    private int currentPathIndex = 0;
    
    private Vector3 worldFrom;
    private Vector3 worldTo;
    private float stepProgress = 1f;
    
    // ---------------- MISIÓN ----------------
    
    private Vector2Int baseGridPos;
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
        grid = GridManager.Instance;
        botManager = BotManager.Instance;
        
        // Posición inicial
        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);
        
        Vector3 pos = transform.position;
        pos.y = fixedYPosition;
        transform.position = pos;
        
        worldFrom = transform.position;
        worldTo = transform.position;
        stepProgress = 1f;
        
        // Registrar en BotManager (necesita un BotController, usamos null por ahora)
        // botManager.RegisterBot(this, CurrentGridPos);
        
        // Guardar base
        if (patologoTransform != null)
        {
            baseGridPos = grid.WorldToGrid(patologoTransform.position);
        }
        else
        {
            baseGridPos = CurrentGridPos;
        }
        
        estado = ScoutState.Idle;
        
        // Debug
        Debug.Log($"{name}: Start completado. useDebugMission = {useDebugMission}");
        if (useDebugMission)
        {
            Debug.Log($"{name}: Iniciando corrutina de misión debug");
            StartCoroutine(IniciarMisionDebugCoroutine());
        }
        else
        {
            Debug.Log($"{name}: useDebugMission está desactivado, esperando misión manual");
        }
    }
    
    private IEnumerator IniciarMisionDebugCoroutine()
    {
        yield return new WaitForSeconds(1f);
        Debug.Log($"{name}: Intentando iniciar misión debug...");
        IniciarMisionDebug();
    }
    
    private void Update()
    {
        // Si está ocupado (escaneando, esperando, etc), no moverse
        if (isBusy)
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
            Debug.LogWarning($"{name}: No se encontró camino desde {CurrentGridPos} hacia {targetGridPos}");
        }
        else
        {
            Debug.Log($"{name}: Ruta calculada hacia {targetGridPos} ({currentPath.Count} pasos)");
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
                    Debug.Log($"{name}: Agregado segmento manual {gridPos} desde {target.name}");
                }
            }
        }
        // Si no hay targets y autoGenerate está activo, buscar plantas
        else if (autoGenerateSegments)
        {
            Debug.Log($"{name}: Generando segmentos automáticamente alrededor de plantas...");
            segmentos = GenerarSegmentosDesdesPlantas();
        }
        else
        {
            Debug.LogWarning($"{name}: No hay debugSegmentTargets y autoGenerateSegments está desactivado!");
            return;
        }
        
        if (segmentos.Count > 0)
        {
            Debug.Log($"{name}: Iniciando misión con {segmentos.Count} segmentos");
            AsignarMision(segmentos);
        }
        else
        {
            Debug.LogWarning($"{name}: No se generaron segmentos válidos");
        }
    }
    
    private List<Vector2Int> GenerarSegmentosDesdesPlantas()
    {
        List<Vector2Int> segmentos = new List<Vector2Int>();
        
        Debug.Log($"{name}: 🔍 Buscando harvest spots en zona {zonaAsignada}...");
        Debug.Log($"{name}: Mi posición actual: {CurrentGridPos}");
        
        // Buscar todos los tiles con isHarvestSpot = true
        TileInfo[] todosTiles = FindObjectsByType<TileInfo>(FindObjectsSortMode.None);
        
        // Calcular el punto medio del mapa en Y
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        
        foreach (var tile in todosTiles)
        {
            if (tile.isHarvestSpot)
            {
                if (tile.gridPos.y < minY) minY = tile.gridPos.y;
                if (tile.gridPos.y > maxY) maxY = tile.gridPos.y;
            }
        }
        
        float midY = (minY + maxY) / 2f;
        Debug.Log($"{name}: Rango Y del mapa: {minY} a {maxY}, Punto medio: {midY:F1}");
        
        // Filtrar según zona asignada
        System.Collections.Generic.List<(Vector2Int pos, float distancia)> candidatos = 
            new System.Collections.Generic.List<(Vector2Int, float)>();
        
        foreach (var tile in todosTiles)
        {
            if (tile.isHarvestSpot)
            {
                bool enMiZona = false;
                
                if (zonaAsignada == ZonaPatrullaje.Top && tile.gridPos.y >= midY)
                {
                    enMiZona = true;
                }
                else if (zonaAsignada == ZonaPatrullaje.Bottom && tile.gridPos.y < midY)
                {
                    enMiZona = true;
                }
                
                if (enMiZona)
                {
                    float dist = Vector2Int.Distance(CurrentGridPos, tile.gridPos);
                    candidatos.Add((tile.gridPos, dist));
                }
            }
        }
        
        Debug.Log($"{name}: Encontrados {candidatos.Count} harvest spots en zona {zonaAsignada}");
        
        // Ordenar por distancia (más cercanos primero)
        candidatos.Sort((a, b) => a.distancia.CompareTo(b.distancia));
        
        // Agregar todos los de mi zona
        for (int i = 0; i < candidatos.Count; i++)
        {
            segmentos.Add(candidatos[i].pos);
            
            if (i < 10)
            {
                Debug.Log($"{name}: ✓ HarvestSpot #{i+1}: {candidatos[i].pos} (Y={candidatos[i].pos.y}, dist: {candidatos[i].distancia:F1})");
            }
        }
        
        Debug.Log($"{name}: 📋 Total: {segmentos.Count} harvest spots en mi zona");
        
        if (segmentos.Count == 0)
        {
            Debug.LogError($"{name}: ¡No se encontraron tiles con isHarvestSpot = true!");
        }
        
        return segmentos;
    }
    
    // ---------------- API MISIÓN ----------------
    
    public void AsignarMision(List<Vector2Int> segmentos)
    {
        if (estado != ScoutState.Idle)
        {
            Debug.LogWarning($"{name}: No puede recibir misión en estado {estado}");
            return;
        }
        
        segmentosPendientes.Clear();
        segmentosInspeccionados.Clear();
        segmentosBloqueados.Clear();
        imagenesCapturadas.Clear();
        
        foreach (var seg in segmentos)
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
        
        // 1. IR AL PATÓLOGO PRIMERO (solo si estoy cerca - para evitar viaje largo innecesario)
        float distanciaBase = Vector2Int.Distance(CurrentGridPos, baseGridPos);
        
        if (baseGridPos != CurrentGridPos && distanciaBase < 20f) // Solo ir si está cerca
        {
            // Verificar si el Patólogo es alcanzable
            List<Vector2Int> pathToBase = grid.FindPath(CurrentGridPos, baseGridPos, null);
            
            if (pathToBase != null && pathToBase.Count > 0)
            {
                Debug.Log($"{name}: Yendo al Patólogo en {baseGridPos} para iniciar misión (dist: {distanciaBase:F1})");
                estado = ScoutState.NavegandoSegmento;
                SetTarget(baseGridPos);
                
                float timeoutBase = 30f;
                float elapsedBase = 0f;
                while (CurrentGridPos != baseGridPos && elapsedBase < timeoutBase)
                {
                    elapsedBase += Time.deltaTime;
                    yield return null;
                }
                
                if (CurrentGridPos == baseGridPos)
                {
                    Debug.Log($"{name}: En base, esperando {baseWaitTime}s antes de iniciar patrullaje");
                    yield return new WaitForSeconds(baseWaitTime);
                }
                else
                {
                    Debug.LogWarning($"{name}: Timeout yendo al Patólogo, comenzando desde posición actual");
                }
            }
            else
            {
                Debug.LogWarning($"{name}: Patólogo en {baseGridPos} no es alcanzable, comenzando desde posición actual");
            }
        }
        else
        {
            Debug.Log($"{name}: Base está lejos (dist: {distanciaBase:F1}) o ya estoy ahí - iniciando patrullaje directo desde {CurrentGridPos}");
            yield return new WaitForSeconds(1f);
        }
        
        // 2. VISITAR SEGMENTOS
        while (segmentosPendientes.Count > 0)
        {
            Vector2Int segmento = segmentosPendientes.Dequeue();
            
            Debug.Log($"{name}: 🚶 Navegando a segmento {segmento} (Y={segmento.y}, X={segmento.x}) desde {CurrentGridPos} - {segmentosPendientes.Count} restantes");
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
                        Debug.LogWarning($"{name}: Atascado en {CurrentGridPos}, abortando segmento {segmento}");
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
                Debug.LogWarning($"{name}: No llegué a {segmento}, saltando...");
                continue;
            }
            
            // LLEGAMOS AL SEGMENTO - DETENER COMPLETAMENTE
            Debug.Log($"{name}: ✓ Llegué al segmento {segmento} - DETENIENDO MOVIMIENTO");
            isBusy = true;
            currentPath.Clear(); // Limpiar ruta para asegurar que no se mueva
            estado = ScoutState.EscaneandoPlanta;
            List<Vector2Int> plantas = DetectarPlantas(segmento);
            string segmentoId = $"Seg_{segmento.x}_{segmento.y}";
            
            if (plantas.Count > 0)
            {
                Debug.Log($"{name}: 📷 DETENIDO - Escaneando {plantas.Count} plantas en {segmentoId}...");
                
                foreach (var planta in plantas)
                {
                    Debug.Log($"{name}: 📷 FOTOGRAFIANDO planta en {planta} - ESPERANDO {captureTime} segundos...");
                    yield return new WaitForSeconds(captureTime);
                    Observacion obs = CapturarImagen(planta, segmentoId);
                    imagenesCapturadas.Add(obs);
                    Debug.Log($"{name}: ✓ Foto capturada!");
                }
                
                Debug.Log($"{name}: ✓ Escaneo completo - {plantas.Count} plantas fotografiadas");
            }
            else
            {
                Debug.Log($"{name}: ⚠ No hay plantas alrededor del segmento {segmentoId}");
            }
            
            segmentosInspeccionados.Add(segmento);
            isBusy = false; // Reanudar movimiento
        }
        
        // 3. REGRESAR A BASE
        Debug.Log($"{name}: 🏠 Regresando al Patólogo en {baseGridPos}");
        estado = ScoutState.RegresandoBase;
        
        // Verificar si la base es alcanzable
        List<Vector2Int> pathToBaseReturn = grid.FindPath(CurrentGridPos, baseGridPos, null);
        
        if (pathToBaseReturn != null && pathToBaseReturn.Count > 0)
        {
            SetTarget(baseGridPos);
            
            float timeoutReturn = 60f;
            float elapsedReturn = 0f;
            while (CurrentGridPos != baseGridPos && elapsedReturn < timeoutReturn)
            {
                elapsedReturn += Time.deltaTime;
                yield return null;
            }
            
            if (CurrentGridPos == baseGridPos)
            {
                Debug.Log($"{name}: ✓ Llegué a la base del Patólogo");
            }
            else
            {
                Debug.LogWarning($"{name}: Timeout regresando a base, transfiriendo desde posición actual");
            }
        }
        else
        {
            Debug.LogWarning($"{name}: Base no alcanzable desde {CurrentGridPos}, transfiriendo desde posición actual");
        }
        
        // 4. TRANSFERIR DATOS
        isBusy = true;
        estado = ScoutState.TransfiriendoDatos;
        Debug.Log($"{name}: 📡 DETENIDO - Transfiriendo datos al Patólogo (esperando {baseWaitTime}s)...");
        yield return new WaitForSeconds(baseWaitTime);
        isBusy = false;
        
        float tiempoTotal = Time.time - tiempoInicio;
        Debug.Log($"{name}: ✓✓✓ MISIÓN COMPLETADA ✓✓✓");
        Debug.Log($"{name}: - Observaciones: {imagenesCapturadas.Count}");
        Debug.Log($"{name}: - Segmentos inspeccionados: {segmentosInspeccionados.Count}");
        Debug.Log($"{name}: - Segmentos bloqueados: {segmentosBloqueados.Count}");
        Debug.Log($"{name}: - Tiempo total: {tiempoTotal:F2}s");
        
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
        if (patologoTransform != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(patologoTransform.position, 0.5f);
        }
    }
}
