using UnityEngine;

/// <summary>
/// Clase base para cualquier agente que se mueva en el grid.
/// No implementa movimiento todavía, solo define el "contrato"
/// mínimo que necesitan los sistemas de pathfinding y prioridad.
/// 
/// Más adelante:
/// - BotController heredará de aquí.
/// - Los nuevos ScoutBots y NurseBots también.
/// </summary>
public abstract class BaseGridBot : MonoBehaviour
{
    /// <summary>
    /// Posición actual del bot en coordenadas de grid.
    /// La dejamos protegida para que la actualicen las subclases
    /// cuando se muevan.
    /// </summary>
    public Vector2Int CurrentGridPos { get; protected set; }

    /// <summary>
    /// Costo aproximado restante de la misión de este bot.
    /// Se usa para decidir prioridades de paso en BotManager.
    /// </summary>
    public abstract float RemainingCost { get; }

    /// <summary>
    /// Indica al bot que debe replantear su ruta.
    /// BotManager la llamará cuando haya conflicto de movimiento.
    /// </summary>
    public abstract void ForceReplan();
}
