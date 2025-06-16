using Godot;
using Godot.Collections;
using Godot.NativeInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Resources;

[Tool]
public partial class Mc : MeshInstance3D
{

    [Export]
    public Vector3I res = new Vector3I(20,20,20);
    private Vector3I grid_res;

    public enum density_code
    {
        Esfera,
        Textura,
        Terreno,
        Cueva,
        Extra1
    }

    [Export]
    public density_code density_function = density_code.Terreno;

    [Export]
    public float influencia = 10;

    [Export]
    public float altura = 10;

    private Vector3I c_res;
    private float c_inf;

    [Export]
    public Texture3D texture;

    public Array<Image> image;
    public bool ignoreTexture = false;

    public bool update = false;

    private float[] voxelGrid;
    private byte[] voxelTexture;

    private Vector3[] vertices = [];
    private Vector3[] normales = [];

    private ulong memoryCount;
    public ulong t_grid, t_uniform, t_sincro, t_lec, t_build, t_total;

    private void setVoxel(int x, int y, int z, float v)
    {
        voxelGrid[x + grid_res.X * (y + grid_res.Y * z)] = v;
    }

    private float getVoxel(int x, int y, int z)
    {
        return voxelGrid[x + grid_res.X * (y + grid_res.Y * z)];
    }

    private Vector3 centro;

    public bool initialize_rd = true;
    public RenderingDevice rd;
    public Rid shader_id, pipeline;

    private float inRange(float v)
    {
        return Math.Max(-1, Math.Min(1, v));
    }

    private float normalizeDensity(float v)
    {
        return (v + 1f) * 0.5f;
    }

    private float cilinderX(int x, int y, int z)
    {
        float dist = Math.Abs(x - grid_res.X/2);
        return dist / 5;
    }
    
    public virtual float density(int x, int y, int z)
    {
        switch (density_function)
        {
            case density_code.Esfera:
                Vector3 p = new Vector3(x, y, z);
                float v = (1 - p.DistanceTo(centro) * 3 * (1f / grid_res.X));
                return inRange(v);
            case density_code.Textura:
                return inRange(image[z].GetPixel(x, y).R) * influencia;
            case density_code.Terreno:
                float a = altura - y + (1 - ((1 - image[z].GetPixel(x, y).R) * 2)) * influencia;
                return inRange(a);
            case density_code.Cueva:
                float mid = grid_res.Y / 2;
                float d = Math.Abs(mid - y)/altura;
                float e = d - 1 + (1 - ((1 - image[z].GetPixel(x, y).R) * 2)) * influencia;
                return inRange(e);
            case density_code.Extra1:
                Vector3 center = grid_res / 2;
                Vector3 forX = new Vector3(x, center.Y, center.Z);
                float distX = forX.DistanceTo(new Vector3(x,y,z));
                Vector3 forY = new Vector3(center.X, y, center.Z);
                float distY = forY.DistanceTo(new Vector3(x, y, z));
                Vector3 forZ = new Vector3(center.X, center.Y, z);
                float distZ = forZ.DistanceTo(new Vector3(x, y, z));
                float closest = Math.Min(distX,Math.Min(distY, distZ));
                return inRange(1 - closest / 10  + (1 - ((1 - image[z].GetPixel(x, y).R) * 2)) * influencia);
        }
        return 0;
    }

    private void initGrid()
    {
        if (!ignoreTexture)
            image = texture.GetData();

        grid_res = res + new Vector3I(1, 1, 1);

        c_res = new Vector3I(res.X, res.Y, res.Z);
        c_inf = influencia;
        centro = new Vector3(grid_res.X / 2f, grid_res.Y / 2f, grid_res.Z / 2f);
        //voxelGrid = new float[grid_res.X * grid_res.Y * grid_res.Z];
        voxelTexture = new byte[grid_res.X * grid_res.Y * grid_res.Z * 2];

        memoryCount += (ulong)(grid_res.X * grid_res.Y * grid_res.Z * 2);

        float v;
        Half tex;
        byte[] tex_bytes;
        int index = 0;
        for (int z = 0; z < grid_res.Z; z++)
            for (int y = 0; y < grid_res.Y; y++)
                for (int x = 0; x < grid_res.X; x++, index++)
                {
                    v = density(x, y, z);
                    //voxelGrid[index] = v;

                    tex = (Half)normalizeDensity(v);
                    tex_bytes = BitConverter.GetBytes(tex);

                    voxelTexture[index * 2] = tex_bytes[0];
                    voxelTexture[index * 2 + 1] = tex_bytes[1];
                }
                    
    }

    private void buildMesh()
    {
        //GD.Print(string.Join(", ", vertices));

        Godot.Collections.Array surfaceArray = [];
        surfaceArray.Resize((int)Mesh.ArrayType.Max);
        surfaceArray[(int)Mesh.ArrayType.Vertex] = vertices;
        surfaceArray[(int)Mesh.ArrayType.Normal] = normales;

        if (Mesh == null)
            Mesh = new ArrayMesh();
        var arrMesh = Mesh as ArrayMesh;
        arrMesh.ClearSurfaces();
        if (vertices.Length > 0)
            arrMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArray);


        voxelTexture = null;
        vertices = null;
        normales = null;
    }

    private void shader_init()
    {
        rd = RenderingServer.CreateLocalRenderingDevice();

        var shaderFile = GD.Load<RDShaderFile>("res://compute.glsl");
        var shaderBytecode = shaderFile.GetSpirV();
        shader_id = rd.ShaderCreateFromSpirV(shaderBytecode);
        
        pipeline = rd.ComputePipelineCreate(shader_id);
    }
    private void shader()
    {
        var start = Time.GetTicksMsec();

        //Textura3D

        var fmt = new RDTextureFormat();
        fmt.Width = (uint)grid_res.X;
        fmt.Height = (uint)grid_res.Y;
        fmt.Depth = (uint)grid_res.Z;
        fmt.UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.SamplingBit;
        fmt.Format = RenderingDevice.DataFormat.R16Sfloat;
        fmt.TextureType = RenderingDevice.TextureType.Type3D;

        var inputSamplerRID = rd.TextureCreate(fmt, new RDTextureView(), [voxelTexture]);
        var samp_state = new RDSamplerState();
        samp_state.UnnormalizedUvw = true;
        var samp = rd.SamplerCreate(samp_state);

        var tex_uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.SamplerWithTexture,
            Binding = 0
        };
        tex_uniform.AddId(samp);
        tex_uniform.AddId(inputSamplerRID);

        //vertices output

        int maxVert = res.X * res.Y * res.Z * 15;
        int max_bytes = maxVert * 16;
        
        byte[] output_vert = new byte[max_bytes];

        memoryCount += (ulong)max_bytes;

        var soutput_vert = rd.StorageBufferCreate((uint)max_bytes, output_vert);

        var out_vert_uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        out_vert_uniform.AddId(soutput_vert);

        //control buffer

        Byte[] controlData = new Byte[8];

        var control_buffer = rd.StorageBufferCreate((uint)8, controlData);

        var control_uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 2
        };
        control_uniform.AddId(control_buffer);

        //normal output

        byte[] output_normal = new byte[max_bytes];

        memoryCount += (ulong)max_bytes;

        var soutput_norm = rd.StorageBufferCreate((uint)max_bytes, output_normal);

        var out_norm_uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 3
        };
        out_norm_uniform.AddId(soutput_norm);

        //PushConstants

        int res_x = grid_res.X; int res_y = grid_res.Y, res_z = grid_res.Z;
        
        Byte[] pushConstants = new byte[16];

        int offset = 0;
        BitConverter.GetBytes(influencia).CopyTo(pushConstants, offset);
        offset += 4;

        BitConverter.GetBytes(res_x).CopyTo(pushConstants, offset);
        offset += 4;
        BitConverter.GetBytes(res_y).CopyTo(pushConstants, offset);
        offset += 4;
        BitConverter.GetBytes(res_z).CopyTo(pushConstants, offset);
        offset += 4;



        var uniformSet = rd.UniformSetCreate([tex_uniform, out_vert_uniform, control_uniform, out_norm_uniform], shader_id, 0);

        var computeList = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(computeList, pipeline);
        rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
        rd.ComputeListSetPushConstant(computeList, pushConstants, 16);
        rd.ComputeListDispatch(computeList, xGroups: (uint)res.X, yGroups: (uint)res.Y, zGroups: (uint)res.Z);
        rd.ComputeListEnd();

        rd.Submit();
        var create = Time.GetTicksMsec();
        rd.Sync();

        var sync = Time.GetTicksMsec();

        var countResult = rd.BufferGetData(control_buffer);
        uint count = BitConverter.ToUInt32(countResult, 0);
        uint vox_count = BitConverter.ToUInt32(countResult, 4);

        var vertex_result = rd.BufferGetData(soutput_vert);
        vertices = new Vector3[count*3];

        memoryCount += 2 * count * 3 * 12;

        for ( int i = 0; i < count*3; i++)
        {
            offset = i * 16;
            float x = BitConverter.ToSingle(vertex_result, offset);
            float y = BitConverter.ToSingle(vertex_result, offset + 4);
            float z = BitConverter.ToSingle(vertex_result, offset + 8);
            vertices[i] = new Vector3(x, y, z);
        }

        var normal_result = rd.BufferGetData(soutput_norm);
        normales = new Vector3[count * 3];

        for (int i = 0; i < count * 3; i++)
        {
            offset = i * 16;
            float x = BitConverter.ToSingle(normal_result, offset);
            float y = BitConverter.ToSingle(normal_result, offset + 4);
            float z = BitConverter.ToSingle(normal_result, offset + 8);
            normales[i] = new Vector3(x, y, z);
        }

        //GD.Print(vox_count);
        //GD.Print(count_grid());
        //GD.Print(count);

        //GD.Print(string.Join(", ", normales));


        //var outputBytes = rd.BufferGetData(buffer_grid);
        //var output = new float[voxelGrid.Length];
        //Buffer.BlockCopy(outputBytes, 0, output, 0, outputBytes.Length);
        //GD.Print("Input: ", string.Join(", ", input));
        //GD.Print("Output: ", string.Join(", ", output));

        var retrieve = Time.GetTicksMsec();

        //rd.FreeRid(ubuffer_vert);
        //rd.FreeRid(ubuffer_edges);
        //rd.FreeRid(sbuffer_tris);
        rd.FreeRid(soutput_vert);
        rd.FreeRid(soutput_norm);
        rd.FreeRid(control_buffer);
        //rd.FreeRid(uniformSet);
        rd.FreeRid(samp);
        rd.FreeRid(inputSamplerRID);


        t_uniform = (create - start);
        t_sincro = (sync - create);
        t_lec = (retrieve - sync);

        GD.Print("Tiempo Creación: " + t_uniform.ToString());
        GD.Print("Tiempo Sincronización: " + t_sincro.ToString());
        GD.Print("Tiempo Lectura: " + t_lec.ToString());
    }

    public override void _ExitTree()
    {
        if (initialize_rd)
        {
            rd.FreeRid(pipeline);
            rd.FreeRid(shader_id);
            rd.Free();
        }
    }

    public override void _Ready()
    {
        memoryCount = 0;

        var start = Time.GetTicksMsec();
        initGrid();
        var time_grid = Time.GetTicksMsec();
        if (initialize_rd)
            shader_init();
        shader();
        var time_shader = Time.GetTicksMsec();
        buildMesh();
        var time_mesh = Time.GetTicksMsec();

        t_grid = (time_grid - start);
        GD.Print("Tiempo initGrid: " + t_grid.ToString());
        t_build = (time_mesh - time_shader);
        GD.Print("Tiempo buildMesh: " + t_build.ToString());
        t_total = (time_mesh - start);
        GD.Print("Tiempo Total: " + t_total.ToString());
        GD.Print("redone");

        Material material = MaterialOverride;
        if (material != null && material.IsClass("ShaderMaterial"))
        {
            ((ShaderMaterial)material).SetShaderParameter("altura", altura);
            ((ShaderMaterial)material).SetShaderParameter("res", grid_res);
        }

        GD.Print("Memoria: " + (memoryCount / Math.Pow(1024, 2)).ToString() + " MB");
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint())
            if( !c_res.Equals(res) || c_inf != influencia)
            {

                memoryCount = 0;
                var start = Time.GetTicksMsec();
                initGrid();
                var time_grid = Time.GetTicksMsec();
                t_grid = (time_grid - start);
                GD.Print("Tiempo initGrid: " + t_grid.ToString());
                shader();
                var time_shader = Time.GetTicksMsec();
                buildMesh();
                var time_mesh = Time.GetTicksMsec();
                t_build = (time_mesh - time_shader);
                GD.Print("Tiempo buildMesh: " + t_build.ToString());
                t_total = (time_mesh - start);
                GD.Print("Tiempo Total: " + t_total.ToString());
                GD.Print("redone");

                Material material = MaterialOverride;
                if (material != null && material.IsClass("ShaderMaterial"))
                {
                    ((ShaderMaterial)material).SetShaderParameter("altura", altura);
                    ((ShaderMaterial)material).SetShaderParameter("res", grid_res);
                }

                GD.Print("Memoria: " + (memoryCount / Math.Pow(1024,2)).ToString() + " MB");

                //GD.Print("Tiempo shader: " + (time_shader - time_grid).ToString());
            }
        if (update)
        {
            update = false;
            initGrid();
            shader();
            buildMesh();
        }

    }
}
