﻿using System.Collections.Generic;
using System.Text.Json;

namespace AssetStudio
{
    public class UnityTexEnv
    {
        public PPtr<Texture> m_Texture;
        public Vector2 m_Scale;
        public Vector2 m_Offset;

        public UnityTexEnv() { }

        public UnityTexEnv(ObjectReader reader)
        {
            m_Texture = new PPtr<Texture>(reader);
            m_Scale = reader.ReadVector2();
            m_Offset = reader.ReadVector2();
        }
    }

    public class UnityPropertySheet
    {
        public List<KeyValuePair<string, UnityTexEnv>> m_TexEnvs;
        public List<KeyValuePair<string, int>> m_Ints;
        public List<KeyValuePair<string, float>> m_Floats;
        public List<KeyValuePair<string, Color>> m_Colors;

        public UnityPropertySheet() { }

        public UnityPropertySheet(ObjectReader reader)
        {
            var version = reader.version;

            int m_TexEnvsSize = reader.ReadInt32();
            m_TexEnvs = new List<KeyValuePair<string, UnityTexEnv>>();
            for (var i = 0; i < m_TexEnvsSize; i++)
            {
                m_TexEnvs.Add(new KeyValuePair<string, UnityTexEnv>(reader.ReadAlignedString(), new UnityTexEnv(reader)));
            }

            if (version >= 2021) //2021.1 and up
            {
                int m_IntsSize = reader.ReadInt32();
                m_Ints = new List<KeyValuePair<string, int>>();
                for (var i = 0; i < m_IntsSize; i++)
                {
                    m_Ints.Add(new KeyValuePair<string, int>(reader.ReadAlignedString(), reader.ReadInt32()));
                }
            }

            int m_FloatsSize = reader.ReadInt32();
            m_Floats = new List<KeyValuePair<string, float>>();
            for (var i = 0; i < m_FloatsSize; i++)
            {
                m_Floats.Add(new KeyValuePair<string, float>(reader.ReadAlignedString(), reader.ReadSingle()));
            }

            int m_ColorsSize = reader.ReadInt32();
            m_Colors = new List<KeyValuePair<string, Color>>();
            for (var i = 0; i < m_ColorsSize; i++)
            {
                m_Colors.Add(new KeyValuePair<string, Color>(reader.ReadAlignedString(), reader.ReadColor4()));
            }
        }
    }

    public sealed class Material : NamedObject
    {
        public PPtr<Shader> m_Shader;
        public UnityPropertySheet m_SavedProperties;

        public Material() { }

        public Material(ObjectReader reader, byte[] type, JsonSerializerOptions jsonOptions) : base(reader)
        {
            var parsedMaterial = JsonSerializer.Deserialize<Material>(type, jsonOptions);
            m_Shader = parsedMaterial.m_Shader;
            m_SavedProperties = parsedMaterial.m_SavedProperties;
        }

        public Material(ObjectReader reader) : base(reader)
        {
            m_Shader = new PPtr<Shader>(reader);

            if (version == 4 && version.Minor >= 1) //4.1 - 4.7.2
            {
                var m_ShaderKeywords = reader.ReadStringArray();
            }

            if (version >= (2021, 2, 18)) //2021.2.18 and up
            {
                var m_ValidKeywords = reader.ReadStringArray();
                var m_InvalidKeywords = reader.ReadStringArray();
            }
            else if (version >= 5) //5.0 - 2021.2.17
            {
                var m_ShaderKeywords = reader.ReadAlignedString();
            }

            if (version >= 5) //5.0 and up
            {
                var m_LightmapFlags = reader.ReadUInt32();
            }

            if (version >= (5, 6)) //5.6 and up
            {
                var m_EnableInstancingVariants = reader.ReadBoolean();
                //var m_DoubleSidedGI = a_Stream.ReadBoolean(); //2017 and up
                reader.AlignStream();
            }

            if (version >= (4, 3)) //4.3 and up
            {
                var m_CustomRenderQueue = reader.ReadInt32();
            }

            if (version >= (5, 1)) //5.1 and up
            {
                var stringTagMapSize = reader.ReadInt32();
                for (var i = 0; i < stringTagMapSize; i++)
                {
                    var first = reader.ReadAlignedString();
                    var second = reader.ReadAlignedString();
                }
            }

            if (version >= (5, 6)) //5.6 and up
            {
                var disabledShaderPasses = reader.ReadStringArray();
            }

            m_SavedProperties = new UnityPropertySheet(reader);

            //vector m_BuildTextureStacks 2020 and up
        }
    }
}
