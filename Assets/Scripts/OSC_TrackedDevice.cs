using SentienceLab.OSC;
using System.Collections.Generic;
using UnityEngine;

public class OSC_TrackedDevice : MonoBehaviour, IOSCVariableContainer
{
	public string Prefix = "/tracked_device";

	public void Start()
	{
		m_pose = new OSC_6DofPoseVariable(Prefix + "/pose", OSC_6DofPoseVariable.EDataFormat.Pos_RotQuat);
	}

	
	public void Update()
	{
		transform.SetLocalPositionAndRotation(m_pose.Position, m_pose.Rotation);
	}


	public List<OSC_Variable> GetOSC_Variables()
	{
		return new List<OSC_Variable>() { m_pose };
	}


	protected OSC_6DofPoseVariable m_pose;
}
