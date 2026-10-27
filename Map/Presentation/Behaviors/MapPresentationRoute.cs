using System;
using UnityEngine;
using UnityEngine.AI;

/// <summary>寻路表现行为的运行时逻辑，负责驱动目标移动。</summary>
public partial class MapPresentationRoute : MapPresentationBehavior
{
    private NavMeshAgent navAgent;
    private Transform parentTransform;
    private int waypointIndex;
    private int travelDirection = 1;
    private bool finished;

    public override void Initialize(GameObject target)
    {
        waypointIndex = 0;
        travelDirection = 1;
        finished = false;
        navAgent = null;
        parentTransform = target != null ? target.transform.parent : null;
        if (target == null) return;
        if (moveMode != MapPresentationMoveMode.NavMesh) return;

        navAgent = target.GetComponent<NavMeshAgent>();
        if (navAgent == null) navAgent = target.AddComponent<NavMeshAgent>();

        navAgent.speed = Mathf.Max(0f, speed);
        // navAgent.angularSpeed = Mathf.Max(360f, speed * 180f);
        navAgent.angularSpeed = 0;//2D寻路不需要转向
        navAgent.acceleration = Mathf.Max(8f, speed * 8f);
        navAgent.stoppingDistance = Mathf.Max(0f, arrivalDistance);
        navAgent.autoBraking = true;
        navAgent.updateRotation = false;
        navAgent.updateUpAxis = false;
        navAgent.baseOffset = 0;
    }

    public override bool Update(GameObject target, float deltaTime)
    {
        if (finished || waypoints == null || waypoints.Length < 2 || target == null)
            return IsFinished();

        if (moveMode == MapPresentationMoveMode.NavMesh) MoveByNavMesh();
        else MoveDirect(target.transform, deltaTime);
        return IsFinished();
    }

    /// <summary>不循环且不返程的路线到达终点后视为结束；其他路线由 Candidate 寿命配置控制。</summary>
    public override bool IsFinished() => finished;

    public override MapPresentationBehavior Clone()
    {
        return new MapPresentationRoute
        {
            waypoints = waypoints == null ? Array.Empty<Vector3>() : (Vector3[])waypoints.Clone(),
            moveMode = moveMode,
            loop = loop,
            reverseOnEnd = reverseOnEnd,
            speed = speed,
            arrivalDistance = arrivalDistance,
        };
    }

    private void MoveDirect(Transform target, float deltaTime)
    {
        if (waypointIndex < 0 || waypointIndex >= waypoints.Length) return;

        var currentPosition = target.localPosition;
        var targetPosition = waypoints[waypointIndex];
        var newPosition = Vector3.MoveTowards(currentPosition, targetPosition, Mathf.Max(0f, speed) * deltaTime);
        target.localPosition = newPosition;

        if (Vector3.Distance(newPosition, targetPosition) <= Mathf.Max(0.01f, arrivalDistance))
            AdvanceWaypoint();
    }

    private void MoveByNavMesh()
    {
        if (navAgent == null) return;

        var targetPosition = GetWorldPoint(waypointIndex);
 
        if (HasReachedDestination() || !navAgent.hasPath && !navAgent.pathPending)
        {
            AdvanceWaypoint();
            if (!finished)
                navAgent.SetDestination(GetWorldPoint(waypointIndex));
            return;
        }

        // 当地图或根节点移动、上一帧采样失败时，重新刷新目标点。
        if (Vector3.Distance(navAgent.destination, targetPosition) > Mathf.Max(0.05f, arrivalDistance))
            navAgent.SetDestination(targetPosition);
    }

    private void AdvanceWaypoint()
    {
        var lastIndex = waypoints.Length - 1;
        if (loop)
        {
            waypointIndex = waypointIndex >= lastIndex ? 0 : waypointIndex + 1;
            return;
        }

        waypointIndex += travelDirection;
        if (waypointIndex <= lastIndex && waypointIndex >= 0) return;

        if (!reverseOnEnd)
        {
            finished = true;
            return;
        }

        travelDirection = -travelDirection;
        waypointIndex = Mathf.Clamp(waypointIndex + travelDirection * 2, 0, lastIndex);
    }

    private Vector3 GetWorldPoint(int index)
    {
        if (index < 0 || index >= waypoints.Length) return Vector3.zero;
        return parentTransform != null ? parentTransform.TransformPoint(waypoints[index]) : waypoints[index];
    }

    private bool HasReachedDestination()
    {
        if (navAgent.pathPending) return false;

        var tolerance = Mathf.Max(arrivalDistance, navAgent.stoppingDistance + 0.02f);
        return navAgent.isOnNavMesh && navAgent.remainingDistance <= tolerance;
    }
}