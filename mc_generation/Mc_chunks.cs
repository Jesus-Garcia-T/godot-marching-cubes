using Godot;
using Godot.Collections;
using System;

[Tool]

public partial class Mc_chunks : MeshInstance3D
{

    [Export]
    public Vector3I chunk_size = new Vector3I(64, 64, 64);

    [Export]
    public Vector3I chunk_number = new Vector3I(3, 1, 3);
    private int total_chunks;
    Vector3I size;

    [Export]
    public Mc.density_code density_function = Mc.density_code.Terreno;

    [Export]
    public float influencia = 10;

    [Export]
    public float altura = 10;

    private Vector3I c_res;
    private float c_inf;

    [Export]
    public Texture3D texture;
    private Array<Image> image;

    private float[,,] substract;
    private bool[] altered;

    [Export]
    public Vector3 carve_test;
    [Export]
    public float carve_size = 50;

    [Export]
    public FastNoiseLite ruido;
    public Vector3 original_offset;

    private RenderingDevice rd;
    private Rid shader_id, pipeline;

    public ulong t_grid, t_uniform, t_sincro, t_lec, t_build, t_total;

    private partial class Chunk : Mc
    {
        private Mc_chunks control;

        Vector3I offset;
        private float[,,] substract;

        public Chunk(Mc_chunks parent, Vector3I pos) : base()
        {
            control = parent;

            res = parent.chunk_size;
            density_function = parent.density_function;
            influencia = parent.influencia;
            altura = parent.altura;

            rd = control.rd;
            shader_id = control.shader_id;
            pipeline = control.pipeline;
            initialize_rd = false;

            offset = pos * res;
            Position = offset;

            ignoreTexture = true;
            //control.ruido.Offset = offset + parent.original_offset;
            //image = control.ruido.GetImage3D(res.X+1, res.Y+1, res.Z+1);

            image = parent.image;
            substract = parent.substract;

            MaterialOverride = parent.MaterialOverride;
        }

        public override float density(int x, int y, int z)
        {
            int off_x = x + offset.X,
                off_y = y + offset.Y,
                off_z = z + offset.Z;
            //GD.Print(image.Count);
            return Math.Min(base.density(off_x, off_y, off_z), 1 - substract[off_x, off_y, off_z]);
            //return base.density(off_x, off_y, off_z) - substract[off_x,off_y,off_z];
        }

    };

    private Chunk[] chunks;

    private void shader_init()
    {
        rd = RenderingServer.CreateLocalRenderingDevice();

        var shaderFile = GD.Load<RDShaderFile>("res://compute.glsl");
        var shaderBytecode = shaderFile.GetSpirV();
        shader_id = rd.ShaderCreateFromSpirV(shaderBytecode);

        pipeline = rd.ComputePipelineCreate(shader_id);
    }

    public override void _ExitTree()
    {
        rd.FreeRid(pipeline);
        rd.FreeRid(shader_id);
        rd.Free();

        for(int i = 0; i < total_chunks; i++)
            chunks[i].Dispose();
    }

    private int lower_safe(float coord)
    {
        return (int)Math.Max(0, Math.Ceiling(coord));
    }

    private int upper_safe(float coord, int axis)
    {
        return (int)Math.Min(size[axis], Math.Ceiling(coord));
    }

    private void update_chunks()
    {
        int c = 0;
        for(int i = 0; i < total_chunks; i++)
        {
            if (altered[i])
            {
                chunks[i].update = true;
                c++;
            }
        }
        GD.Print(c);
    }
    private void alter(Vector3I chunk)
    {
        int index = chunk.Z + chunk_number.Z * (chunk.Y + chunk_number.Y * chunk.X);
        altered[index] = true;
    }

    private bool chunkExists(Vector3I chunk)
    {
        return (chunk >= new Vector3I(0, 0, 0)
            && chunk.X < chunk_number.X
            && chunk.Y < chunk_number.Y
            && chunk.Z < chunk_number.Z);
    }
    private void carve_terrain(Vector3 center, float radius)
    {
        Vector3I chunkCenter = (Vector3I)(center / chunk_size);
        Vector3 localPos = center.PosMod(chunk_size);

        Vector3I min_chunk = (Vector3I)((localPos - new Vector3(radius, radius, radius)) / chunk_size);
        Vector3I max_chunk = (Vector3I)((localPos + new Vector3(radius, radius, radius)) / chunk_size);

        GD.Print(chunkCenter);
        GD.Print(min_chunk);
        GD.Print(max_chunk);

        for (int i = min_chunk.X; i <= max_chunk.X; i++)
            for (int j = min_chunk.Y; j <= max_chunk.Y; j++)
                for (int k = min_chunk.Z; k <= max_chunk.Z; k++)
                {
                    Vector3I currentChunk = chunkCenter + new Vector3I(i, j, k);
                    if (chunkExists(currentChunk))
                    {
                        int check1 = 0 + (i == 0 ? 1 : 0) + (j == 0 ? 1 : 0) + (k == 0 ? 1 : 0);
                        //GD.Print(check1);
                        if (check1 >= 2)
                            alter(currentChunk);
                        else
                        {
                            Vector3 closest = new Vector3I();
                            for (int a = 0; a < 3; a++)
                            {
                                if (currentChunk[a] == chunkCenter[a])
                                    closest[a] = center[a];
                                else if (currentChunk[a] < chunkCenter[a])
                                    closest[a] = (currentChunk[a] + 1) * chunk_size[a];
                                else
                                    closest[a] = (currentChunk[a]) * chunk_size[a];
                            }

                            if (center.DistanceTo(closest) <= radius)
                                alter(currentChunk);
                        }
                    }
                   
                }

        for (int i = lower_safe(center.X - radius); i < upper_safe(center.X + radius, 0); i++)
            for (int j = lower_safe(center.Y - radius); j < upper_safe(center.Y + radius, 1); j++)
                for (int k = lower_safe(center.Z - radius); k < upper_safe(center.Z + radius, 2); k++)
                {
                    float distance = center.DistanceTo(new Vector3(i,j,k));
                    substract[i, j, k] += (radius - distance) / radius * 2;
                }

        update_chunks();
    }

    private void initialize_chunk_grid()
    {
        t_grid = t_uniform = t_sincro = t_lec = t_build = t_total = 0;

        var start = Time.GetTicksMsec();

        total_chunks = chunk_number.X * chunk_number.Y * chunk_number.Z;
        chunks = new Chunk[total_chunks];
        altered = new bool[total_chunks];
        int index = 0;
        for (int i = 0; i < chunk_number.X; i++)
            for (int j = 0; j < chunk_number.Y; j++)
                for (int k = 0; k < chunk_number.Z; k++, index++)
                {
                    chunks[index] = new Chunk(this, new Vector3I(i, j, k));
                    AddChild(chunks[index]);

                    t_grid += chunks[index].t_grid;
                    t_uniform += chunks[index].t_uniform;
                    t_sincro += chunks[index].t_sincro;
                    t_lec += chunks[index].t_lec;
                    t_build += chunks[index].t_build;
                }

        var end = Time.GetTicksMsec();
        t_total = (end - start);

        GD.Print("Tiempo initGrid: " + t_grid.ToString());
        GD.Print("Tiempo uniform: " + t_uniform.ToString());
        GD.Print("Tiempo sincro: " + t_sincro.ToString());
        GD.Print("Tiempo lectura: " + t_lec.ToString());
        GD.Print("Tiempo buildMesh: " + t_build.ToString());
        GD.Print("Tiempo Total: " + t_total.ToString());
    }
    public override void _Ready()
    {
        base._Ready();

        shader_init();

        size = chunk_number * chunk_size;
        substract = new float[size.X + 1, size.Y + 1, size.Z + 1];
        //carve_terrain(carve_test, carve_size);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (image == null || image.Count == 0)
        {
            image = texture.GetData();

            if (image.Count > 0)
            {
                initialize_chunk_grid();
            }
        }

        if (Input.IsActionJustPressed("ui_accept") && !Engine.IsEditorHint())
        {
            carve_terrain(carve_test, carve_size);
        }
    }
}
