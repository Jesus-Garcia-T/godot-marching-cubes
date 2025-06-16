#if TOOLS
using Godot;
using System;

[Tool]
public partial class mc_plugin : EditorPlugin
{
	public override void _EnterTree()
	{
		var texture = GD.Load<Texture2D>("res://addons/mc_generation/MeshInstance3D.svg");

        var script_mc = GD.Load<Script>("res://addons/mc_generation/Mc.cs");
        var script_chunks = GD.Load<Script>("res://addons/mc_generation/Mc_chunks.cs");

		AddCustomType("VolumeMesh", "MeshInstance3D", script_mc, texture);
        AddCustomType("VolumeMeshSections", "MeshInstance3D", script_chunks, texture);

    }

	public override void _ExitTree()
	{
		RemoveCustomType("VolumeMesh");
		RemoveCustomType("VolumeMeshSections");
	}
}
#endif
