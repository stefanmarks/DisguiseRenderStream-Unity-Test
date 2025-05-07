using SentienceLab.OSC;
using System.Collections.Generic;
using UnityEngine;

public class OSC_TrackedDevice : MonoBehaviour, IOSCVariableContainer
{
	public enum EUpdateType { 
		[InspectorName("Update")]                Update,
		[InspectorName("LateUpdate")]            LateUpdate,
		[InspectorName("Update and LateUpdate")] UpdateAndLateUpdate 
	};

	public string      Prefix            = "/tracked_device";
	
	public EUpdateType UpdateType        = EUpdateType.Update;

	public bool        ApplyTrackedState = false;


	public void Awake()
	{
		m_pose    = new OSC_6DofPoseVariable(Prefix + "/pose",    OSC_6DofPoseVariable.EDataFormat.Pos_RotQuat);
		
		m_tracked = new OSC_BoolVariable(    Prefix + "/tracked");
		m_tracked.OnDataReceived += OnTrackedStateChanged;
		if (ApplyTrackedState) OnTrackedStateChanged(m_tracked); // force inactive
	}

	
	public void Update()
	{
		if ((UpdateType == EUpdateType.Update) || (UpdateType == EUpdateType.UpdateAndLateUpdate))
		{
			transform.SetLocalPositionAndRotation(m_pose.Position, m_pose.Rotation);
		}
	}


	public void LateUpdate()
	{
		if ((UpdateType == EUpdateType.LateUpdate) || (UpdateType == EUpdateType.UpdateAndLateUpdate))
		{
			transform.SetLocalPositionAndRotation(m_pose.Position, m_pose.Rotation);
		}
	}


	public void OnTrackedStateChanged(OSC_Variable var)
	{
		if (ApplyTrackedState)
		{
			this.gameObject.SetActive(((OSC_BoolVariable)var).Value);
		}
	}


	public List<OSC_Variable> GetOSC_Variables()
	{
		return new List<OSC_Variable>() { m_pose, m_tracked };
	}


	protected OSC_6DofPoseVariable m_pose;
	protected OSC_BoolVariable     m_tracked;
}
