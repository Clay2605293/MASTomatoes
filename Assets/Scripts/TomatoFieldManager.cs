using System.Collections.Generic;
using UnityEngine;

public class TomatoFieldManager : MonoBehaviour
{
    public static TomatoFieldManager Instance { get; private set; }

    [System.Serializable]
    public class TomatoTask
    {
        public Vector2Int standPos;  // tile donde se para el bot
        public Vector2Int plantPos;  // tile de la cama con planta
        public int tomatoes;         // tomates restantes (lógicos)

        // Tomates visuales instanciados en la planta
        public List<GameObject> tomatoVisuals = new List<GameObject>();

        // Estado de enfermedad de cada tomate (paralelo a tomatoVisuals)
        // Aquí vamos a guardar la "verdad" médica (si ese tomate viene de planta realmente enferma)
        public List<bool> diseasedTomatoes = new List<bool>();

        // Marca si la planta "parece sospechosa" al sistema de diagnóstico inicial
        public bool appearsSuspicious;

        // Marca si la planta está realmente enferma (ground truth)
        public bool isTrulySick;

        // Marcador visual encima de la planta realmente enferma (flecha roja, etc.)
        [HideInInspector]
        public GameObject sickMarkerInstance;
    }

    [Header("Tareas de cosecha")]
    public List<TomatoTask> allTasks = new List<TomatoTask>();

    private GridManager grid;
    private bool tasksBuilt = false;

    [Header("Visual de tomates")]
    public GameObject tomatoPrefab;        // tu esfera/bolita de tomate (normal)
    public float tomatoHeightStep = 0.15f; // separación en altura entre tomates
    public float tomatoRadiusOffset = 0.15f; // pequeño offset random en X/Z

    [Header("Tomates enfermos (visual)")]
    [Range(0f, 1f)]
    public float diseaseProbability = 0.20f; // ya no la usamos como antes, pero la dejamos por si luego la necesitas
    public GameObject sickTomatoPrefab;      // prefab para tomates "marcados" (p. ej. verdes)

    [Header("Probabilidades de sospecha / enfermedad real")]
    [Tooltip("Probabilidad de que una planta PAREZCA sospechosa (5% = 0.05)")]
    [Range(0f, 1f)]
    public float suspiciousPlantProbability = 0.05f;

    [Tooltip("Dentro de las sospechosas, probabilidad de que sí estén realmente enfermas (80% = 0.8)")]
    [Range(0f, 1f)]
    public float trulySickGivenSuspiciousProbability = 0.8f;

    [Header("Marcadores de plantas realmente enfermas")]
    [Tooltip("Prefab de la flecha roja (o icono) para plantas realmente enfermas.")]
    public GameObject sickMarkerPrefab;

    [Tooltip("Altura a la que se coloca el marcador sobre la planta.")]
    public float sickMarkerHeightOffset = 2.0f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        grid = GridManager.Instance;
        EnsureTasksBuilt();
    }

    // Garantiza que las tareas se construyan solo una vez
    public void EnsureTasksBuilt()
    {
        if (tasksBuilt) return;
        BuildTasksFromTiles();
        tasksBuilt = true;
    }

    private void BuildTasksFromTiles()
    {
        allTasks.Clear();

        if (grid == null)
            grid = GridManager.Instance;

        int suspiciousCount = 0;
        int trulySickCount = 0;

        // Recorremos todos los tiles del grid
        foreach (var kvp in grid.AllTiles)
        {
            Vector2Int pos = kvp.Key;
            TileInfo tile = kvp.Value;

            if (!tile.isHarvestSpot)
                continue;

            // Buscar planta vecina en 4 direcciones
            Vector2Int[] dirs =
            {
                Vector2Int.up,
                Vector2Int.down,
                Vector2Int.left,
                Vector2Int.right
            };

            Vector2Int? plantPos = null;

            foreach (var d in dirs)
            {
                Vector2Int neighbor = pos + d;
                TileInfo neighborTile = grid.GetTile(neighbor);
                if (neighborTile == null) continue;

                if (!neighborTile.walkable && neighborTile.hasPlant)
                {
                    plantPos = neighbor;
                    break;
                }
            }

            if (plantPos == null)
            {
                Debug.LogWarning(
                    $"HarvestSpot en {pos} no encontró planta vecina. Revisa layout."
                );
                continue;
            }

            // Crear tarea con tomates aleatorios
            TomatoTask task = new TomatoTask
            {
                standPos = pos,
                plantPos = plantPos.Value,
                tomatoes = Random.Range(1, 6),
                tomatoVisuals = new List<GameObject>(),
                diseasedTomatoes = new List<bool>(),
                appearsSuspicious = false,
                isTrulySick = false,
                sickMarkerInstance = null
            };

            // Determinar sospecha y enfermedad real a nivel planta
            float suspicionRoll = Random.value;
            if (suspicionRoll < suspiciousPlantProbability)
            {
                task.appearsSuspicious = true;
                suspiciousCount++;

                // Dentro de las sospechosas, probabilidad de estar realmente enfermas
                float truthRoll = Random.value;
                task.isTrulySick = truthRoll < trulySickGivenSuspiciousProbability;
                if (task.isTrulySick) trulySickCount++;
            }
            else
            {
                task.appearsSuspicious = false;
                task.isTrulySick = false;
            }

            // Instanciar tomates visuales sobre la planta
            SpawnTomatoesForTask(task);

            allTasks.Add(task);
        }

        Debug.Log(
            $"TomatoFieldManager: generadas {allTasks.Count} tareas de cosecha. " +
            $"Sospechosas={suspiciousCount}, Realmente enfermas={trulySickCount}."
        );
    }

    private void SpawnTomatoesForTask(TomatoTask task)
    {
        if (tomatoPrefab == null)
        {
            Debug.LogWarning("TomatoFieldManager: tomatoPrefab no asignado en el inspector.");
            return;
        }

        if (grid == null)
            grid = GridManager.Instance;

        // Posición base en mundo de la planta
        Vector3 basePos = grid.GridToWorld(task.plantPos);

        for (int i = 0; i < task.tomatoes; i++)
        {
            // VERDAD MÉDICA:
            //   - Si la planta es realmente enferma, este tomate cuenta como enfermo para la lógica médica.
            //   - Si no, es sano.
            bool isMedicallySick = task.isTrulySick;

            // APARIENCIA VISUAL:
            //   - Si la planta aparece sospechosa, usamos el prefab alternativo (sickTomatoPrefab)
            //     para que destaque en el invernadero.
            bool useSuspiciousPrefab = task.appearsSuspicious;

            // Guardamos la verdad médica en la lista paralela
            task.diseasedTomatoes.Add(isMedicallySick);

            // Pequeño random en X/Z para que no queden perfectamente alineados
            float offsetX = Random.Range(-tomatoRadiusOffset, tomatoRadiusOffset);
            float offsetZ = Random.Range(-tomatoRadiusOffset, tomatoRadiusOffset);
            float offsetY = (i + 0.5f) * tomatoHeightStep;

            Vector3 spawnPos = basePos + new Vector3(offsetX, offsetY, offsetZ);

            // Seleccionar prefab según apariencia (sospechosa -> prefab alterno)
            GameObject prefabToUse = tomatoPrefab;

            if (useSuspiciousPrefab && sickTomatoPrefab != null)
            {
                prefabToUse = sickTomatoPrefab;
            }

            GameObject go = Instantiate(prefabToUse, spawnPos, Quaternion.identity, transform);
            task.tomatoVisuals.Add(go);
        }
    }

    /// <summary>
    /// Consume visualmente 1 tomate de la tarea dada.
    /// También decrementa el contador lógico de tomates.
    /// Retorna true si el tomate consumido era médicamente enfermo.
    /// </summary>
    public bool ConsumeTomato(TomatoTask task)
    {
        if (task == null) return false;
        if (task.tomatoes <= 0) return false;

        task.tomatoes--;

        bool wasSick = false;

        // Eliminar estado de enfermedad del último tomate (verdad médica)
        if (task.diseasedTomatoes != null && task.diseasedTomatoes.Count > 0)
        {
            int idx = task.diseasedTomatoes.Count - 1;
            wasSick = task.diseasedTomatoes[idx];
            task.diseasedTomatoes.RemoveAt(idx);
        }

        // Eliminar el visual del último tomate
        if (task.tomatoVisuals != null && task.tomatoVisuals.Count > 0)
        {
            int idx = task.tomatoVisuals.Count - 1;
            GameObject go = task.tomatoVisuals[idx];
            task.tomatoVisuals.RemoveAt(idx);

            if (go != null)
                Destroy(go);
        }

        return wasSick;
    }

    /// <summary>
    /// Instancia una flecha roja (o el prefab que asignes) sobre cada planta realmente enferma.
    /// Pensado para ser llamado por el Orchestrator al terminar el diagnóstico.
    /// </summary>
    public void ShowTrulySickMarkers(IEnumerable<TomatoTask> sickTasks)
    {
        if (sickMarkerPrefab == null)
        {
            Debug.LogWarning("[TomatoFieldManager] sickMarkerPrefab no asignado. No se pueden mostrar marcadores de plantas enfermas.");
            return;
        }

        if (grid == null)
            grid = GridManager.Instance;

        if (grid == null)
        {
            Debug.LogWarning("[TomatoFieldManager] No hay GridManager. No se pueden posicionar marcadores.");
            return;
        }

        foreach (var task in sickTasks)
        {
            if (task == null) continue;

            // Si ya tiene marcador, no duplicamos
            if (task.sickMarkerInstance != null) continue;

            Vector3 basePos = grid.GridToWorld(task.plantPos);
            basePos += Vector3.up * sickMarkerHeightOffset;

            GameObject marker = Instantiate(sickMarkerPrefab, basePos, Quaternion.identity, this.transform);
            task.sickMarkerInstance = marker;
        }

        Debug.Log("[TomatoFieldManager] Marcadores de plantas realmente enfermas activados.");
    }

    /// <summary>
    /// Elimina todos los marcadores rojos de plantas enfermas.
    /// Útil si reinicias el escenario.
    /// </summary>
    public void ClearTrulySickMarkers()
    {
        if (allTasks == null) return;

        foreach (var task in allTasks)
        {
            if (task != null && task.sickMarkerInstance != null)
            {
                Destroy(task.sickMarkerInstance);
                task.sickMarkerInstance = null;
            }
        }
    }
}
