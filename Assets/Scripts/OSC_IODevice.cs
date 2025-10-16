using SentienceLab.OSC;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class OSC_IODevice : MonoBehaviour, IOSCVariableContainer
{
	[System.Serializable]
	public struct InputToEventMap
	{
		public string           OSC_Name;
		public UnityEvent<bool> OnInputActive;
	}

	[System.Serializable]
	public struct Output
	{
		public string OSC_Name;
	}

	public string Prefix = "/io_device";

	public List<InputToEventMap> Inputs;
	public List<Output>          Outputs;


	protected void Initialise()
	{
		if (m_inputs == null)
		{
			m_inputs = new List<OSC_BoolVariable>();

			foreach (var input in Inputs)
			{
				OSC_BoolVariable oscVar = new OSC_BoolVariable(Prefix + "/" + input.OSC_Name);
				oscVar.OnDataReceived += var => { OnUpdate(var, input.OnInputActive); };
				m_inputs.Add(oscVar);
			}
		}

		if (m_outputs == null)
		{
			m_outputs = new List<OSC_BoolVariable>();

			foreach (var output in Outputs)
			{
				OSC_BoolVariable oscVar = new OSC_BoolVariable(Prefix + "/" + output.OSC_Name);
				m_outputs.Add(oscVar);
			}
		}

		if (m_allVars == null)
		{
			m_allVars = new List<OSC_Variable>();
			m_allVars.AddRange(m_inputs);
			m_allVars.AddRange(m_outputs);
		}
	}


	public void Awake()
	{
		Initialise();
	}


	protected void OnUpdate(OSC_Variable _var, UnityEvent<bool> _event)
	{
		_event.Invoke(((OSC_BoolVariable) _var).Value);
	}


	public void Update()
	{
		// nothing to do here
	}


	public List<OSC_Variable> GetOSC_Variables()
	{
		Initialise();
		return m_allVars;
	}


	public void SetOutput1(bool value)
	{
		if (m_outputs != null)
		{
			if (m_outputs.Count > 0)
			{
				m_outputs[0].Value = value;
				m_outputs[0].SendUpdate();
			}
		}
	}


	protected List<OSC_BoolVariable> m_inputs  = null;
	protected List<OSC_BoolVariable> m_outputs = null;
	protected List<OSC_Variable>     m_allVars = null;
}
