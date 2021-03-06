﻿using AdvUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace RNNSharp
{

    public class LSTMGateWeight
    {
        public float[][] weights;
        public float[][] deri;
        public float[][] learningRate;
        private int layerSize;
        private int denseFeatureSize;
        protected ParallelOptions parallelOption = new ParallelOptions();

        public void CleanLearningRate()
        {
            learningRate = new float[layerSize][];

            Parallel.For(0, layerSize, parallelOption, i =>
            {

                if (denseFeatureSize > 0)
                {
                    learningRate[i] = new float[denseFeatureSize];
                }
            });
        }

        public void Init(int LayerSize, int DenseFeatureSize, bool hasDeri = true)
        {
            layerSize = LayerSize;
            denseFeatureSize = DenseFeatureSize;

            weights = new float[LayerSize][];

            if (hasDeri)
            {
                deri = new float[LayerSize][];
            }
            else
            {
                deri = null;
            }

            for (var i = 0; i < LayerSize; i++)
            {
                weights[i] = new float[DenseFeatureSize];

                if (hasDeri)
                {
                    deri[i] = new float[DenseFeatureSize];
                }
                for (var j = 0; j < DenseFeatureSize; j++)
                {
                    weights[i][j] = RNNHelper.RandInitWeight();
                }
            }
        }
    }

    public class LSTMLayer : SimpleLayer
    {
        public LSTMCell[] cell;
        protected Vector4[] cellLearningRate;
        protected Vector3[] peepholeLearningRate;

        //Due to different data access patterns(random access for sparse features and continuous access for dense features), 
        //we use different data structure to keep features weights in order to improve performance 
        protected LSTMGateWeight wDenseInputGate;
        protected LSTMGateWeight wDenseForgetGate;
        protected LSTMGateWeight wDenseCellGate;
        protected LSTMGateWeight wDenseOutputGate;

        //X - wInputInputGate
        //Y - wInputForgetGate
        //Z - wInputCell
        //W - wInputOutputGate
        protected Vector4[][] sparseFeatureWeights;
        protected Vector3[][] sparseFeatureToHiddenDeri;
        protected Vector4[][] sparseFeatureToHiddenLearningRate;


        private Vector4 vecMaxGrad;

        private Vector3 vecMaxGrad3;
        private Vector4 vecMinGrad;
        private Vector3 vecMinGrad3;

        private Vector4 vecNormalLearningRate;
        private Vector3 vecNormalLearningRate3;
        private Vector<float> vecNormalLearningRateFloat;

        public LSTMLayer(LSTMLayerConfig config) : base(config)
        {
            AllocateMemoryForLSTMCells();
        }

        public LSTMLayer()
        {
            LayerConfig = new LayerConfig();
        }

        public void AllocateMemoryForLSTMCells()
        {
            cell = new LSTMCell[LayerSize];
            for (var i = 0; i < LayerSize; i++)
            {
                cell[i] = new LSTMCell();
            }
        }

        public override void InitializeWeights(int sparseFeatureSize, int denseFeatureSize)
        {
            SparseFeatureSize = sparseFeatureSize;
            DenseFeatureSize = denseFeatureSize;

            CreateCell(null);

            if (SparseFeatureSize > 0)
            {
                sparseFeatureWeights = new Vector4[LayerSize][];
                sparseFeatureToHiddenDeri = new Vector3[LayerSize][];
                for (var i = 0; i < LayerSize; i++)
                {
                    sparseFeatureWeights[i] = new Vector4[SparseFeatureSize];
                    sparseFeatureToHiddenDeri[i] = new Vector3[SparseFeatureSize];
                    for (var j = 0; j < SparseFeatureSize; j++)
                    {
                        sparseFeatureWeights[i][j] = InitializeLSTMWeight();
                    }
                }
            }

            if (DenseFeatureSize > 0)
            {
                wDenseInputGate = new LSTMGateWeight();
                wDenseForgetGate = new LSTMGateWeight();
                wDenseCellGate = new LSTMGateWeight();
                wDenseOutputGate = new LSTMGateWeight();
                wDenseInputGate.Init(LayerSize, DenseFeatureSize);
                wDenseForgetGate.Init(LayerSize, DenseFeatureSize);
                wDenseCellGate.Init(LayerSize, DenseFeatureSize);
                wDenseOutputGate.Init(LayerSize, DenseFeatureSize, false);
            }

            Logger.WriteLine(
                "Initializing weights, sparse feature size: {0}, dense feature size: {1}, random value is {2}",
                SparseFeatureSize, DenseFeatureSize, RNNHelper.rand.NextDouble());
        }

        private void CreateCell(BinaryReader br)
        {
            if (br != null)
            {
                //Load weight from input file
                for (var i = 0; i < LayerSize; i++)
                {
                    cell[i].wPeepholeIn = br.ReadDouble();
                    cell[i].wPeepholeForget = br.ReadDouble();
                    cell[i].wPeepholeOut = br.ReadDouble();

                    cell[i].wCellIn = br.ReadDouble();
                    cell[i].wCellForget = br.ReadDouble();
                    cell[i].wCellState = br.ReadDouble();
                    cell[i].wCellOut = br.ReadDouble();
                }
            }
            else
            {
                //Initialize weight by random number
                for (var i = 0; i < LayerSize; i++)
                {
                    //internal weights, also important
                    cell[i].wPeepholeIn = RNNHelper.RandInitWeight();
                    cell[i].wPeepholeForget = RNNHelper.RandInitWeight();
                    cell[i].wPeepholeOut = RNNHelper.RandInitWeight();

                    cell[i].wCellIn = RNNHelper.RandInitWeight();
                    cell[i].wCellForget = RNNHelper.RandInitWeight();
                    cell[i].wCellState = RNNHelper.RandInitWeight();
                    cell[i].wCellOut = RNNHelper.RandInitWeight();
                }
            }
        }

        private Vector4 InitializeLSTMWeight()
        {
            Vector4 w;

            //initialise each weight to random value
            w.X = RNNHelper.RandInitWeight();
            w.Y = RNNHelper.RandInitWeight();
            w.Z = RNNHelper.RandInitWeight();
            w.W = RNNHelper.RandInitWeight();

            return w;
        }

        private double TanH(double x)
        {
            return Math.Tanh(x);
        }

        private double TanHDerivative(double x)
        {
            var tmp = Math.Tanh(x);
            return 1 - tmp * tmp;
        }

        private double Sigmoid(double x)
        {
            return 1.0 / (1.0 + Math.Exp(-x));
        }

        private double SigmoidDerivative(double x)
        {
            return Sigmoid(x) * (1.0 - Sigmoid(x));
        }

        private void SaveHiddenLayerWeights(BinaryWriter fo)
        {
            for (var i = 0; i < LayerSize; i++)
            {
                fo.Write(cell[i].wPeepholeIn);
                fo.Write(cell[i].wPeepholeForget);
                fo.Write(cell[i].wPeepholeOut);

                fo.Write(cell[i].wCellIn);
                fo.Write(cell[i].wCellForget);
                fo.Write(cell[i].wCellState);
                fo.Write(cell[i].wCellOut);
            }
        }

        private void SaveLSTMweights(LSTMGateWeight gateWeight, BinaryWriter fo, bool bVQ = false)
        {
            float[][] weights = gateWeight.weights;
            var w = weights.Length;
            var h = weights[0].Length;

            Logger.WriteLine($"Saving LSTM gate weight matrix. width: {w}, height: {h}");

            fo.Write(w);
            fo.Write(h);

            fo.Write(0);

            for (var i = 0; i < w; i++)
            {
                for (var j = 0; j < h; j++)
                {
                    fo.Write(weights[i][j]);
                }
            }
        }

        private void SaveLSTMWeights(Vector4[][] weight, BinaryWriter fo, bool bVQ = false)
        {
            var w = weight.Length;
            var h = weight[0].Length;
            var vqSize = 256;

            Logger.WriteLine("Saving LSTM weight matrix. width:{0}, height:{1}, vq:{2}", w, h, bVQ);

            fo.Write(weight.Length);
            fo.Write(weight[0].Length);

            if (bVQ == false)
            {
                fo.Write(0);

                for (var i = 0; i < w; i++)
                {
                    for (var j = 0; j < h; j++)
                    {
                        fo.Write(weight[i][j].X);
                        fo.Write(weight[i][j].Y);
                        fo.Write(weight[i][j].Z);
                        fo.Write(weight[i][j].W);
                    }
                }
            }
            else
            {
                //Build vector quantization model
                var vqInputCell = new VectorQuantization();
                var vqInputForgetGate = new VectorQuantization();
                var vqInputInputGate = new VectorQuantization();
                var vqInputOutputGate = new VectorQuantization();
                for (var i = 0; i < w; i++)
                {
                    for (var j = 0; j < h; j++)
                    {
                        vqInputInputGate.Add(weight[i][j].X);
                        vqInputForgetGate.Add(weight[i][j].Y);
                        vqInputCell.Add(weight[i][j].Z);
                        vqInputOutputGate.Add(weight[i][j].W);
                    }
                }

                var distortion = vqInputInputGate.BuildCodebook(vqSize);
                Logger.WriteLine("InputInputGate distortion: {0}", distortion);

                distortion = vqInputForgetGate.BuildCodebook(vqSize);
                Logger.WriteLine("InputForgetGate distortion: {0}", distortion);

                distortion = vqInputCell.BuildCodebook(vqSize);
                Logger.WriteLine("InputCell distortion: {0}", distortion);

                distortion = vqInputOutputGate.BuildCodebook(vqSize);
                Logger.WriteLine("InputOutputGate distortion: {0}", distortion);

                fo.Write(vqSize);

                //Save InputInputGate VQ codebook into file
                for (var j = 0; j < vqSize; j++)
                {
                    fo.Write(vqInputInputGate.CodeBook[j]);
                }

                //Save InputForgetGate VQ codebook into file
                for (var j = 0; j < vqSize; j++)
                {
                    fo.Write(vqInputForgetGate.CodeBook[j]);
                }

                //Save InputCell VQ codebook into file
                for (var j = 0; j < vqSize; j++)
                {
                    fo.Write(vqInputCell.CodeBook[j]);
                }

                //Save InputOutputGate VQ codebook into file
                for (var j = 0; j < vqSize; j++)
                {
                    fo.Write(vqInputOutputGate.CodeBook[j]);
                }

                for (var i = 0; i < w; i++)
                {
                    for (var j = 0; j < h; j++)
                    {
                        fo.Write((byte)vqInputInputGate.ComputeVQ(weight[i][j].X));
                        fo.Write((byte)vqInputForgetGate.ComputeVQ(weight[i][j].Y));
                        fo.Write((byte)vqInputCell.ComputeVQ(weight[i][j].Z));
                        fo.Write((byte)vqInputOutputGate.ComputeVQ(weight[i][j].W));
                    }
                }
            }
        }

        private LSTMGateWeight LoadLSTMGateWeights(BinaryReader br)
        {
            var w = br.ReadInt32();
            var h = br.ReadInt32();
            var vqSize = br.ReadInt32();
            LSTMGateWeight gateWeight = new LSTMGateWeight();

            Logger.WriteLine("Loading LSTM-Weight: width:{0}, height:{1}, vqSize:{2}...", w, h, vqSize);

            var m = new float[w][];
            gateWeight.weights = m;

            for (var i = 0; i < w; i++)
            {
                m[i] = new float[h];
                for (var j = 0; j < h; j++)
                {
                    m[i][j] = br.ReadSingle();
                }
            }

            return gateWeight;
        }

        private Vector4[][] LoadLSTMWeights(BinaryReader br)
        {
            var w = br.ReadInt32();
            var h = br.ReadInt32();
            var vqSize = br.ReadInt32();
            var m = new Vector4[w][];

            Logger.WriteLine("Loading LSTM-Weight: width:{0}, height:{1}, vqSize:{2}...", w, h, vqSize);
            if (vqSize == 0)
            {
                for (var i = 0; i < w; i++)
                {
                    m[i] = new Vector4[h];
                    for (var j = 0; j < h; j++)
                    {
                        m[i][j].X = br.ReadSingle();
                        m[i][j].Y = br.ReadSingle();
                        m[i][j].Z = br.ReadSingle();
                        m[i][j].W = br.ReadSingle();
                    }
                }
            }
            else
            {
                var codeBookInputCell = new List<float>();
                var codeBookInputForgetGate = new List<float>();
                var codeBookInputInputGate = new List<float>();
                var codeBookInputOutputGate = new List<float>();

                for (var i = 0; i < vqSize; i++)
                {
                    codeBookInputInputGate.Add(br.ReadSingle());
                }

                for (var i = 0; i < vqSize; i++)
                {
                    codeBookInputForgetGate.Add(br.ReadSingle());
                }

                for (var i = 0; i < vqSize; i++)
                {
                    codeBookInputCell.Add(br.ReadSingle());
                }

                for (var i = 0; i < vqSize; i++)
                {
                    codeBookInputOutputGate.Add(br.ReadSingle());
                }

                for (var i = 0; i < w; i++)
                {
                    m[i] = new Vector4[h];
                    for (var j = 0; j < h; j++)
                    {
                        int vqIdx = br.ReadByte();
                        m[i][j].X = codeBookInputInputGate[vqIdx];

                        vqIdx = br.ReadByte();
                        m[i][j].Y = codeBookInputForgetGate[vqIdx];

                        vqIdx = br.ReadByte();
                        m[i][j].Z = codeBookInputCell[vqIdx];

                        vqIdx = br.ReadByte();
                        m[i][j].W = codeBookInputOutputGate[vqIdx];
                    }
                }
            }

            return m;
        }

        public override void Save(BinaryWriter fo)
        {
            fo.Write(LayerSize);
            fo.Write(SparseFeatureSize);
            fo.Write(DenseFeatureSize);

            //Save hidden layer weights
            Logger.WriteLine(
                $"Saving LSTM layer, size = '{LayerSize}', sparse feature size = '{SparseFeatureSize}', dense feature size = '{DenseFeatureSize}'");

            SaveHiddenLayerWeights(fo);

            if (SparseFeatureSize > 0)
            {
                //weight input->hidden
                Logger.WriteLine("Saving sparse feature weights...");
                SaveLSTMWeights(sparseFeatureWeights, fo);
            }

            if (DenseFeatureSize > 0)
            {
                //weight fea->hidden
                Logger.WriteLine("Saving dense feature weights...");

                SaveLSTMweights(wDenseInputGate, fo);
                SaveLSTMweights(wDenseCellGate, fo);
                SaveLSTMweights(wDenseForgetGate, fo);
                SaveLSTMweights(wDenseOutputGate, fo);
            }
        }

        public override void Load(BinaryReader br)
        {
            LayerSize = br.ReadInt32();
            SparseFeatureSize = br.ReadInt32();
            DenseFeatureSize = br.ReadInt32();

            AllocateMemoryForCells();
            AllocateMemoryForLSTMCells();

            //Create cells of each layer
            CreateCell(br);

            //Load weight matrix between each two layer pairs
            //weight input->hidden
            if (SparseFeatureSize > 0)
            {
                Logger.WriteLine("Loading sparse feature weights...");
                sparseFeatureWeights = LoadLSTMWeights(br);
            }

            if (DenseFeatureSize > 0)
            {
                //weight fea->hidden
                Logger.WriteLine("Loading dense feature weights...");
                wDenseInputGate = LoadLSTMGateWeights(br);
                wDenseCellGate = LoadLSTMGateWeights(br);
                wDenseForgetGate = LoadLSTMGateWeights(br);
                wDenseOutputGate = LoadLSTMGateWeights(br);
            }
        }

        public override void CleanLearningRate()
        {
            if (SparseFeatureSize > 0)
            {
                sparseFeatureToHiddenLearningRate = new Vector4[LayerSize][];
            }

            if (DenseFeatureSize > 0)
            {
                wDenseCellGate.CleanLearningRate();
                wDenseForgetGate.CleanLearningRate();
                wDenseInputGate.CleanLearningRate();
                wDenseOutputGate.CleanLearningRate();
            }

            peepholeLearningRate = new Vector3[LayerSize];
            cellLearningRate = new Vector4[LayerSize];
            Parallel.For(0, LayerSize, parallelOption, i =>
            {
                if (SparseFeatureSize > 0)
                {
                    sparseFeatureToHiddenLearningRate[i] = new Vector4[SparseFeatureSize];
                }
            });

            vecNormalLearningRate = new Vector4(RNNHelper.LearningRate, RNNHelper.LearningRate, RNNHelper.LearningRate,
                RNNHelper.LearningRate);
            vecNormalLearningRate3 = new Vector3(RNNHelper.LearningRate, RNNHelper.LearningRate, RNNHelper.LearningRate);

            vecNormalLearningRateFloat = new Vector<float>(RNNHelper.LearningRate);

            vecMaxGrad = new Vector4(RNNHelper.GradientCutoff, RNNHelper.GradientCutoff, RNNHelper.GradientCutoff,
                RNNHelper.GradientCutoff);
            vecMinGrad = new Vector4(-RNNHelper.GradientCutoff, -RNNHelper.GradientCutoff, -RNNHelper.GradientCutoff,
                -RNNHelper.GradientCutoff);

            vecMaxGrad3 = new Vector3(RNNHelper.GradientCutoff, RNNHelper.GradientCutoff, RNNHelper.GradientCutoff);
            vecMinGrad3 = new Vector3(-RNNHelper.GradientCutoff, -RNNHelper.GradientCutoff, -RNNHelper.GradientCutoff);
        }

        // forward process. output layer consists of tag value
        public override void ForwardPass(SparseVector sparseFeature, float[] denseFeature, bool isTrain = true)
        {
            //inputs(t) -> hidden(t)
            //Get sparse feature and apply it into hidden layer
            SparseFeature = sparseFeature;
            DenseFeature = denseFeature;

            Parallel.For(0, LayerSize, parallelOption, j =>
            {
                var cell_j = cell[j];

                //hidden(t-1) -> hidden(t)
                cell_j.previousCellState = cell_j.cellState;
                previousCellOutput[j] = Cell[j];

                var vecCell_j = Vector4.Zero;

                if (SparseFeatureSize > 0)
                {
                    //Apply sparse weights
                    var weights = sparseFeatureWeights[j];
                    foreach (var pair in SparseFeature)
                    {
                        vecCell_j += weights[pair.Key] * pair.Value;
                    }
                }

                if (DenseFeatureSize > 0)
                {
                    //Apply dense weights
                    var k = 0;
                    float[] denseInputGateWeight_j = wDenseInputGate.weights[j];
                    float[] denseForgetGateWeight_j = wDenseForgetGate.weights[j];
                    float[] denseCellGateWeight_j = wDenseCellGate.weights[j];
                    float[] denseOutputGateWeight_j = wDenseOutputGate.weights[j];
                    while (k < DenseFeatureSize - Vector<float>.Count)
                    {
                        var vX = new Vector<float>(denseInputGateWeight_j, k);
                        var vY = new Vector<float>(denseForgetGateWeight_j, k);
                        var vZ = new Vector<float>(denseCellGateWeight_j, k);
                        var vW = new Vector<float>(denseOutputGateWeight_j, k);
                        var vFeature = new Vector<float>(DenseFeature, k);

                        vecCell_j.X += Vector.Dot(vX, vFeature);
                        vecCell_j.Y += Vector.Dot(vY, vFeature);
                        vecCell_j.Z += Vector.Dot(vZ, vFeature);
                        vecCell_j.W += Vector.Dot(vW, vFeature);

                        k += Vector<float>.Count;
                    }

                    while (k < DenseFeatureSize)
                    {
                        vecCell_j.X += denseInputGateWeight_j[k] * DenseFeature[k];
                        vecCell_j.Y += denseForgetGateWeight_j[k] * DenseFeature[k];
                        vecCell_j.Z += denseCellGateWeight_j[k] * DenseFeature[k];
                        vecCell_j.W += denseOutputGateWeight_j[k] * DenseFeature[k];
                        k++;
                    }
                }

                //rest the value of the net input to zero
                cell_j.netIn = vecCell_j.X;
                cell_j.netForget = vecCell_j.Y;
                //reset each netCell state to zero
                cell_j.netCellState = vecCell_j.Z;
                //reset each netOut to zero
                cell_j.netOut = vecCell_j.W;

                var cell_j_previousCellOutput = previousCellOutput[j];

                //include internal connection multiplied by the previous cell state
                cell_j.netIn += cell_j.previousCellState * cell_j.wPeepholeIn + cell_j_previousCellOutput * cell_j.wCellIn;
                //squash input
                cell_j.yIn = Sigmoid(cell_j.netIn);

                //include internal connection multiplied by the previous cell state
                cell_j.netForget += cell_j.previousCellState * cell_j.wPeepholeForget +
                                    cell_j_previousCellOutput * cell_j.wCellForget;
                cell_j.yForget = Sigmoid(cell_j.netForget);

                cell_j.netCellState += cell_j_previousCellOutput * cell_j.wCellState;
                cell_j.yCellState = TanH(cell_j.netCellState);

                //cell state is equal to the previous cell state multipled by the forget gate and the cell inputs multiplied by the input gate
                cell_j.cellState = cell_j.yForget * cell_j.previousCellState + cell_j.yIn * cell_j.yCellState;

                ////include the internal connection multiplied by the CURRENT cell state
                cell_j.netOut += cell_j.cellState * cell_j.wPeepholeOut + cell_j_previousCellOutput * cell_j.wCellOut;

                //squash output gate
                cell_j.yOut = Sigmoid(cell_j.netOut);

                Cell[j] = (float)(TanH(cell_j.cellState) * cell_j.yOut);

                cell[j] = cell_j;
            });
        }


        private void UpdateOutputGateWeights(LSTMGateWeight gateWeight, int i, float err)
        {
            var j = 0;
            float[] learningrate_i = gateWeight.learningRate[i];
            float[] weights_i = gateWeight.weights[i];
            while (j < DenseFeatureSize - Vector<float>.Count)
            {
                Vector<float> vecDelta = new Vector<float>(DenseFeature, j);
                vecDelta = vecDelta * err;
                vecDelta = RNNHelper.NormalizeGradient(vecDelta);
                var wlr_i = new Vector<float>(learningrate_i, j);
                var vecLearningRate = ComputeLearningRate(vecDelta, ref wlr_i);

                var w_i = new Vector<float>(weights_i, j);
                w_i += vecLearningRate * vecDelta;

                w_i.CopyTo(weights_i, j);
                wlr_i.CopyTo(learningrate_i, j);

                j += Vector<float>.Count;
            }

            while (j < DenseFeatureSize)
            {
                float delta = DenseFeature[j] * err;
                delta = RNNHelper.NormalizeGradient(delta);
                var wlr_i = learningrate_i[j];
                var learningRate = ComputeLearningRate(delta, ref wlr_i);

                weights_i[j] += learningRate * delta;
                learningrate_i[j] = wlr_i;

                j++;
            }
        }

        private void UpdateGateWeights(int curState, LSTMGateWeight gateWeight, int i, float featureDerivate, float c_yForget, float err)
        {
            var j = 0;
            float[] deri_i = gateWeight.deri[i];
            float[] learningrate_i = gateWeight.learningRate[i];
            float[] weights_i = gateWeight.weights[i];
            while (j < DenseFeatureSize - Vector<float>.Count)
            {
                var feature = new Vector<float>(DenseFeature, j);
                var wd = feature * featureDerivate;
                if (curState > 0)
                {
                    var wd_i = new Vector<float>(deri_i, j);
                    wd += wd_i * c_yForget;
                }
                wd.CopyTo(deri_i, j);

                Vector<float> vecDelta = wd * err;
                vecDelta = RNNHelper.NormalizeGradient(vecDelta);
                var wlr_i = new Vector<float>(learningrate_i, j);
                var vecLearningRate = ComputeLearningRate(vecDelta, ref wlr_i);

                var w_i = new Vector<float>(weights_i, j);
                w_i += vecLearningRate * vecDelta;

                w_i.CopyTo(weights_i, j);
                wlr_i.CopyTo(learningrate_i, j);

                j += Vector<float>.Count;
            }

            while (j < DenseFeatureSize)
            {
                var wd = DenseFeature[j] * featureDerivate;
                if (curState > 0)
                {
                    wd += deri_i[j] * c_yForget;
                }
                deri_i[j] = wd;

                float delta = wd * err;
                delta = RNNHelper.NormalizeGradient(delta);
                var wlr_i = learningrate_i[j];
                var learningRate = ComputeLearningRate(delta, ref wlr_i);

                weights_i[j] += learningRate * delta;
                learningrate_i[j] = wlr_i;

                j++;
            }
        }

        public override void BackwardPass(int numStates, int curState)
        {
            //put variables for derivaties in weight class and cell class
            Parallel.For(0, LayerSize, parallelOption, i =>
            {
                var c = cell[i];

                //using the error find the gradient of the output gate
                var gradientOutputGate = (float)(SigmoidDerivative(c.netOut) * TanH(c.cellState) * Err[i]);

                //internal cell state error
                var cellStateError =
                    (float)(c.yOut * Err[i] * TanHDerivative(c.cellState) + gradientOutputGate * c.wPeepholeOut);

                var vecErr = new Vector4(cellStateError, cellStateError, cellStateError, gradientOutputGate);

                var Sigmoid2_ci_netCellState_mul_SigmoidDerivative_ci_netIn = TanH(c.netCellState) *
                                                                              SigmoidDerivative(c.netIn);
                var ci_previousCellState_mul_SigmoidDerivative_ci_netForget = c.previousCellState *
                                                                              SigmoidDerivative(c.netForget);
                var Sigmoid2Derivative_ci_netCellState_mul_ci_yIn = TanHDerivative(c.netCellState) * c.yIn;

                var vecDerivate = new Vector3(
                    (float)Sigmoid2_ci_netCellState_mul_SigmoidDerivative_ci_netIn,
                    (float)ci_previousCellState_mul_SigmoidDerivative_ci_netForget,
                    (float)Sigmoid2Derivative_ci_netCellState_mul_ci_yIn);
                var c_yForget = (float)c.yForget;

                if (SparseFeatureSize > 0)
                {
                    //Get sparse feature and apply it into hidden layer
                    var w_i = sparseFeatureWeights[i];
                    var wd_i = sparseFeatureToHiddenDeri[i];
                    var wlr_i = sparseFeatureToHiddenLearningRate[i];

                    foreach (var entry in SparseFeature)
                    {
                        var wd = vecDerivate * entry.Value;
                        if (curState > 0)
                        {
                            //Adding historical information
                            wd += wd_i[entry.Key] * c_yForget;
                        }
                        wd_i[entry.Key] = wd;

                        //Computing final err delta
                        var vecDelta = new Vector4(wd, entry.Value);
                        vecDelta = vecErr * vecDelta;
                        vecDelta = Vector4.Clamp(vecDelta, vecMinGrad, vecMaxGrad);

                        //Computing actual learning rate
                        var vecLearningRate = ComputeLearningRate(vecDelta, ref wlr_i[entry.Key]);
                        w_i[entry.Key] += vecLearningRate * vecDelta;
                    }
                }

                if (DenseFeatureSize > 0)
                {
                    UpdateGateWeights(curState, wDenseInputGate, i, vecDerivate.X, c_yForget, cellStateError);
                    UpdateGateWeights(curState, wDenseForgetGate, i, vecDerivate.Y, c_yForget, cellStateError);
                    UpdateGateWeights(curState, wDenseCellGate, i, vecDerivate.Z, c_yForget, cellStateError);
                    UpdateOutputGateWeights(wDenseOutputGate, i, gradientOutputGate);
                }

                //Update peephols weights
                //partial derivatives for internal connections
                c.dSWPeepholeIn = c.dSWPeepholeIn * c.yForget +
                                  Sigmoid2_ci_netCellState_mul_SigmoidDerivative_ci_netIn * c.previousCellState;

                //partial derivatives for internal connections, initially zero as dS is zero and previous cell state is zero
                c.dSWPeepholeForget = c.dSWPeepholeForget * c.yForget +
                                      ci_previousCellState_mul_SigmoidDerivative_ci_netForget * c.previousCellState;

                //update internal weights
                var vecCellDelta = new Vector3((float)c.dSWPeepholeIn, (float)c.dSWPeepholeForget, (float)c.cellState);
                var vecErr3 = new Vector3(cellStateError, cellStateError, gradientOutputGate);

                vecCellDelta = vecErr3 * vecCellDelta;

                //Normalize err by gradient cut-off
                vecCellDelta = Vector3.Clamp(vecCellDelta, vecMinGrad3, vecMaxGrad3);

                //Computing actual learning rate
                var vecCellLearningRate = ComputeLearningRate(vecCellDelta, ref peepholeLearningRate[i]);

                vecCellDelta = vecCellLearningRate * vecCellDelta;

                c.wPeepholeIn += vecCellDelta.X;
                c.wPeepholeForget += vecCellDelta.Y;
                c.wPeepholeOut += vecCellDelta.Z;

                //Update cells weights
                var c_previousCellOutput = previousCellOutput[i];
                //partial derivatives for internal connections
                c.dSWCellIn = c.dSWCellIn * c.yForget +
                              Sigmoid2_ci_netCellState_mul_SigmoidDerivative_ci_netIn * c_previousCellOutput;

                //partial derivatives for internal connections, initially zero as dS is zero and previous cell state is zero
                c.dSWCellForget = c.dSWCellForget * c.yForget +
                                  ci_previousCellState_mul_SigmoidDerivative_ci_netForget * c_previousCellOutput;

                c.dSWCellState = c.dSWCellState * c.yForget +
                                 Sigmoid2Derivative_ci_netCellState_mul_ci_yIn * c_previousCellOutput;

                var vecCellDelta4 = new Vector4((float)c.dSWCellIn, (float)c.dSWCellForget, (float)c.dSWCellState,
                    c_previousCellOutput);
                vecCellDelta4 = vecErr * vecCellDelta4;

                //Normalize err by gradient cut-off
                vecCellDelta4 = Vector4.Clamp(vecCellDelta4, vecMinGrad, vecMaxGrad);

                //Computing actual learning rate
                var vecCellLearningRate4 = ComputeLearningRate(vecCellDelta4, ref cellLearningRate[i]);

                vecCellDelta4 = vecCellLearningRate4 * vecCellDelta4;

                c.wCellIn += vecCellDelta4.X;
                c.wCellForget += vecCellDelta4.Y;
                c.wCellState += vecCellDelta4.Z;
                c.wCellOut += vecCellDelta4.W;

                cell[i] = c;
            });
        }

        public override void ComputeLayerErr(SimpleLayer nextLayer, float[] destErrLayer, float[] srcErrLayer)
        {
            var layer = nextLayer as LSTMLayer;

            if (layer != null)
            {
                Parallel.For(0, LayerSize, parallelOption, i =>
                {
                    var err = 0.0f;
                    for (var k = 0; k < nextLayer.LayerSize; k++)
                    {
                        err += srcErrLayer[k] * layer.wDenseOutputGate.weights[k][i];
                    }
                    destErrLayer[i] = RNNHelper.NormalizeGradient(err);
                });
            }
            else
            {
                base.ComputeLayerErr(nextLayer, destErrLayer, srcErrLayer);
            }
        }

        public override void ComputeLayerErr(SimpleLayer nextLayer)
        {
            var layer = nextLayer as LSTMLayer;

            if (layer != null)
            {
                Parallel.For(0, LayerSize, parallelOption, i =>
                {
                    var err = 0.0f;
                    for (var k = 0; k < nextLayer.LayerSize; k++)
                    {
                        err += layer.Err[k] * layer.wDenseOutputGate.weights[k][i];
                    }
                    Err[i] = RNNHelper.NormalizeGradient(err);
                });
            }
            else
            {
                base.ComputeLayerErr(nextLayer);
            }
        }

        public override void Reset(bool updateNet = false)
        {
            for (var i = 0; i < LayerSize; i++)
            {
                Cell[i] = 0;
                InitializeLSTMCell(cell[i]);
            }
        }

        private void InitializeLSTMCell(LSTMCell c)
        {
            c.previousCellState = 0;
            c.cellState = 0;

            //partial derivatives
            c.dSWPeepholeIn = 0;
            c.dSWPeepholeForget = 0;

            c.dSWCellIn = 0;
            c.dSWCellForget = 0;
            c.dSWCellState = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 ComputeLearningRate(Vector3 vecDelta, ref Vector3 vecWeightLearningRate)
        {
            vecWeightLearningRate += vecDelta * vecDelta;
            return vecNormalLearningRate3 / (Vector3.SquareRoot(vecWeightLearningRate) + Vector3.One);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector4 ComputeLearningRate(Vector4 vecDelta, ref Vector4 vecWeightLearningRate)
        {
            vecWeightLearningRate += vecDelta * vecDelta;
            return vecNormalLearningRate / (Vector4.SquareRoot(vecWeightLearningRate) + Vector4.One);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector<float> ComputeLearningRate(Vector<float> vecDelta, ref Vector<float> vecWeightLearningRate)
        {
            vecWeightLearningRate += vecDelta * vecDelta;

            return vecNormalLearningRateFloat / (Vector.SquareRoot(vecWeightLearningRate) + Vector<float>.One);

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ComputeLearningRate(float delta, ref float weightLearningRate)
        {
            weightLearningRate += delta * delta;

            return (float)(RNNHelper.LearningRate / (Math.Sqrt(weightLearningRate) + 1.0));
        }
    }

    public class LSTMCell
    {
        public double cellState;
        public double dSWCellForget;

        public double dSWCellIn;
        public double dSWCellState;
        public double dSWPeepholeForget;

        //partial derivatives
        public double dSWPeepholeIn;

        //cell state
        public double netCellState;

        //forget gate
        public double netForget;

        //input gate
        public double netIn;

        //output gate
        public double netOut;

        public double previousCellState;
        public double wCellForget;

        public double wCellIn;
        public double wCellOut;
        public double wCellState;
        public double wPeepholeForget;

        //internal weights and deltas
        public double wPeepholeIn;

        public double wPeepholeOut;
        public double yCellState;
        public double yForget;
        public double yIn;
        public double yOut;
    }
}