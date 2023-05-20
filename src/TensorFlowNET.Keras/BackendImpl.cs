﻿/*****************************************************************************
   Copyright 2018 The TensorFlow.NET Authors. All Rights Reserved.

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
******************************************************************************/

using Tensorflow.NumPy;
using System;
using System.Linq;
using System.Collections.Generic;
using Tensorflow.Functions;
using Tensorflow.Graphs;
using static Tensorflow.Binding;
using static Tensorflow.Graphs.SubGraphUtility;
using Tensorflow.Util;
using Tensorflow.Operations;
using OneOf;

namespace Tensorflow.Keras
{
    public class BackendImpl : BackendBase
    {
        /* ----------------------------------------  KERAS BACKEND NATIVE OBJECTS  ---------------------------------------- */
        public Func<Array, double> py_sum = sum;
        public Func<Array, bool> py_all = all;
        //Func<Array, bool> py_any = any;
        //Func<double, double, double, IEnumerable<double>> py_slice = slice;

        public Session _SESSION => ops.get_default_session();

        public Graph _GRAPH;
        FuncGraph _CURRENT_SCRATCH_GRAPH;
        public Dictionary<Graph, GraphLearningPhase> _GRAPH_LEARNING_PHASES;
        //Dictionary<Graph, Dictionary<string, int>> PER_GRAPH_LAYER_NAME_UIDS;
        public bool _MANUAL_VAR_INIT = false;
        public List<string> _LOCAL_DEVICES = null;
        /* --------------------------------------  KERAS BACKEND NATIVE OBJECTS END  -------------------------------------- */

        /// <summary>
        /// A global dictionary mapping graph objects to an index of counters used
        /// for various layer names in each graph.
        /// Allows to give unique autogenerated names to layers, in a graph-specific way.
        /// </summary>
        public Dictionary<Graph, Dictionary<string, int>> PER_GRAPH_LAYER_NAME_UIDS = new Dictionary<Graph, Dictionary<string, int>>();
        public Dictionary<string, IVariableV1> _GRAPH_VARIABLES = new Dictionary<string, IVariableV1>();
        public Dictionary<string, Optimizer> _GRAPH_TF_OPTIMIZERS = new Dictionary<string, Optimizer>();

        public _DummyEagerGraph _DUMMY_EAGER_GRAPH = new _DummyEagerGraph();

        public BackendImpl()
        {
        }

        public void track_variable(IVariableV1 v)
        {
            if (tf.Context.executing_eagerly())
            {
                return;
            }
            var graph = v.Graph;
            if (graph is null)
            {
                graph = get_graph();
            }
            _GRAPH_VARIABLES[graph.graph_key] = v;
        }

        public Tensor placeholder(Shape shape = null,
            int ndim = -1,
            TF_DataType dtype = TF_DataType.DtInvalid,
            bool sparse = false,
            string name = null,
            bool ragged = false)
        {
            if (sparse)
            {
                throw new NotImplementedException("placeholder sparse is true");
            }
            else
            {
                return array_ops.placeholder(dtype: dtype, shape: shape, name: name);
            }
        }

        public Graph get_graph()
        {
            if (tf.Context.executing_eagerly())
            {
                if (_GRAPH == null)
                    _GRAPH = new FuncGraph("keras_graph");

                return _GRAPH;
            }
            return ops.get_default_graph();
        }

        FuncGraph _scratch_graph()
        {
            if (_CURRENT_SCRATCH_GRAPH == null)
                _CURRENT_SCRATCH_GRAPH = new FuncGraph("keras_scratch_graph");

            return _CURRENT_SCRATCH_GRAPH;
        }

        public int get_uid(string prefix)
        {
            var graph = tf.get_default_graph();
            if (!PER_GRAPH_LAYER_NAME_UIDS.ContainsKey(graph))
                PER_GRAPH_LAYER_NAME_UIDS.Add(graph, new defaultdict<string, int>());
            if (!PER_GRAPH_LAYER_NAME_UIDS[graph].ContainsKey(prefix))
                PER_GRAPH_LAYER_NAME_UIDS[graph][prefix] = 0;
            PER_GRAPH_LAYER_NAME_UIDS[graph][prefix] += 1;

            return PER_GRAPH_LAYER_NAME_UIDS[graph][prefix];
        }

        public void reset_uids() => PER_GRAPH_LAYER_NAME_UIDS = new Dictionary<Graph, Dictionary<string, int>>();
        public void clear_session()
        {
            tf.Context.reset_context();
            reset_uids();
            // var phase = tf.placeholder_with_default(false, new int[] { }, name: "keras_learning_phase");
            if (_GRAPH_LEARNING_PHASES != null)
                _GRAPH_LEARNING_PHASES.Clear();
            if (_GRAPH_LEARNING_PHASES != null)
                _GRAPH_LEARNING_PHASES.Clear();
            PER_GRAPH_LAYER_NAME_UIDS.Clear();
            _CURRENT_SCRATCH_GRAPH = null;
            _GRAPH = null;

            ops.set_default_session(tf.Session(ops.get_default_graph()));
            tf.enable_eager_execution();
            tf.Runner.ClearEagerOperationMap();

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        public void manual_variable_initialization(bool value)
        {
            _MANUAL_VAR_INIT = value;
        }

        public Tensor mean(Tensor x, int axis = -1, bool keepdims = false)
        {
            if (x.dtype.as_base_dtype() == TF_DataType.TF_BOOL)
                x = math_ops.cast(x, TF_DataType.TF_FLOAT);
            return math_ops.reduce_mean(x, axis: axis, keepdims: false);
        }

        public GraphLearningPhase learning_phase()
        {
            var graph = tf.get_default_graph();
            if (_GRAPH_LEARNING_PHASES.ContainsKey(graph))
            {
                var phase = tf.placeholder_with_default(false, shape: new int[] { }, name: "keras_learning_phase");
                _GRAPH_LEARNING_PHASES[graph] = 0;
            }
            return _GRAPH_LEARNING_PHASES[graph];
        }
        public void set_learning_phase(bool value)
        {
            _GRAPH_LEARNING_PHASES[tf.get_default_graph()] = (GraphLearningPhase)((value) ? 1 : 0);
        }

        public void set_value(IVariableV1 x, object value)
        {
            // TODO(Rinne): check the implementation.
            x.assign(value);
        }

        public void batch_set_value(List<(IVariableV1, NDArray)> tuples)
        {
            if (ops.executing_eagerly_outside_functions())
            {
                foreach (var (x, value) in tuples)
                    x.assign(value, read_value: false);
            }
            else
            {
                throw new NotImplementedException("");
            }
        }

        /// <summary>
        /// Pads the 2nd and 3rd dimensions of a 4D tensor.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="padding"></param>
        /// <param name="data_format"></param>
        /// <returns></returns>
        public Tensor spatial_2d_padding(Tensor x, NDArray padding = null, string data_format = null)
        {
            if (padding == null)
                padding = new[,] { { 1, 1 }, { 1, 1 } };

            NDArray pattern;

            if (data_format == "channels_first")
                pattern = new int[,]
                {
                    { 0, 0 },
                    { 0, 0 },
                    { padding[0][0], padding[0][1] },
                    { padding[1][0], padding[1][1] }
                };
            else
                pattern = new int[,]
                {
                    { 0, 0 },
                    { padding[0][0], padding[0][1] },
                    { padding[1][0], padding[1][1] },
                    { 0, 0 }
                };
            return array_ops.pad(x, pattern);
        }

        /// <summary>
        /// Method to evaluate a tensor in eager or in a tf.function.
        /// </summary>
        /// <param name="outputs"></param>
        /// <returns></returns>
        public NDArray eval_in_eager_or_function(Tensors outputs)
        {
            if (outputs[0].op.type == "Const")
                return tensor_util.constant_value(outputs);

            var source_graph = outputs.graph;
            var exec_graph = _scratch_graph();
            var global_graph = get_graph();
            if (source_graph == global_graph && exec_graph != global_graph)
            {
                var lifted_map = lift_to_graph(outputs, exec_graph,
                    new List<Tensor>(),
                    add_sources: true,
                    handle_captures: true,
                    base_graph: source_graph);
            }
            if (outputs[0].op.type == "Placeholder"
                || outputs[0].op.type == "StridedSlice")
                return exec_graph.external_captures.Last().numpy();

            // Consolidate updates
            exec_graph.as_default();
            exec_graph.Inputs = exec_graph.internal_captures;
            exec_graph.Outputs = outputs;

            var graph_fn = new ConcreteFunction(exec_graph);

            _CURRENT_SCRATCH_GRAPH = null;
            tf.Context.restore_mode();
            // return outputs.eval();
            throw new NotImplementedException("");
        }

        public class _DummyEagerGraph
        { }

        /// <summary>
        /// Categorical crossentropy between an output tensor and a target tensor.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="output"></param>
        /// <param name="from_logits"></param>
        /// <param name="axis"></param>
        /// <returns></returns>
        public Tensor categorical_crossentropy(Tensor target, Tensor output, bool from_logits = false, int axis = -1)
        {
            if (from_logits)
                return tf.nn.softmax_cross_entropy_with_logits_v2(labels: target, logits: output, axis: axis);

            if (output.op != null && output.op.type == "Softmax")
            {
                if (output.op.inputs.Length != 1) throw new ApplicationException();
                var o = output.op.inputs[0];
                return tf.nn.softmax_cross_entropy_with_logits_v2(labels: target, logits: o, axis: axis);
            }

            // scale preds so that the class probas of each sample sum to 1
            output = output / math_ops.reduce_sum(output, new Axis(axis), true);
            // Compute cross entropy from probabilities.
            var epsilon_ = constant_op.constant(epsilon(), output.dtype.as_base_dtype());
            output = clip_ops.clip_by_value(output, epsilon_, 1.0f - epsilon_);
            return -math_ops.reduce_sum(target * math_ops.log(output), new Axis(axis));
        }

        public Tensor sparse_categorical_crossentropy(Tensor target, Tensor output, bool from_logits = false, int axis = -1, int? ignore_class = null)
        {
            target = tf.cast(target, tf.int64);
            if (!from_logits)
            {
                var epsilon_ = constant_op.constant(epsilon(), output.dtype.as_base_dtype());
                output = tf.clip_by_value(output, epsilon_, 1 - epsilon_);
                output = tf.math.log(output);
            }
            var output_rank = output.shape.ndim;
            if (output_rank > -1)
            {
                axis = Math.Abs(axis) % output_rank;
                if (axis != output_rank - 1)
                {
                    /*var permutation = list(
                        itertools.chain(
                            range(axis), range(axis + 1, output_rank), [axis]
                        )
                    );
                    output = tf.transpose(output, perm: permutation);*/
                    throw new NotImplementedException("");
                }

            }

            var output_shape = tf.shape(output);
            var target_rank = target.shape.ndim;
            var update_shape = target_rank > -1 && output_rank > -1 && target_rank != output_rank - 1;
            if (update_shape)
            {
                target = tf.reshape(target, -1);
                output = tf.reshape(output, (-1, output.shape[-1]));
            }

            if (ignore_class.HasValue)
            {
                throw new NotImplementedException("");
            }

            var res = tf.nn.sparse_softmax_cross_entropy_with_logits(labels: target, logits: output);

            if (ignore_class.HasValue)
            {
                throw new NotImplementedException("");
            }

            if (update_shape && output_rank >= 3)
            {
                // If our output includes timesteps or
                // spatial dimensions we need to reshape
                res = tf.reshape(res, output_shape[":-1"]);
            }

            return res;
        }

        public Tensor binary_crossentropy(Tensor target, Tensor output, bool from_logits = false)
        {
            if (from_logits)
                return tf.nn.sigmoid_cross_entropy_with_logits(labels: target, logits: output);

            var epsilon_ = constant_op.constant(epsilon(), dtype: output.dtype.as_base_dtype());
            output = tf.clip_by_value(output, epsilon_, 1.0f - epsilon_);

            // Compute cross entropy from probabilities.
            var bce = target * tf.math.log(output + epsilon());
            bce += (1 - target) * tf.math.log(1 - output + epsilon());
            return -bce;
        }

        /// <summary>
        /// Resizes the images contained in a 4D tensor.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="height_factor"></param>
        /// <param name="width_factor"></param>
        /// <param name="data_format"></param>
        /// <param name="interpolation"></param>
        /// <returns></returns>
        public Tensor resize_images(Tensor x, int height_factor, int width_factor,
            string data_format, string interpolation = "nearest")
        {
            var (rows, cols) = (0, 0);
            if (data_format == "channels_first")
                (rows, cols) = (2, 3);
            else if (data_format == "channels_last")
                (rows, cols) = (1, 2);
            else
                throw new ValueError($"Invalid `data_format` argument: {data_format}");

            var original_shape = x.shape;
            var new_shape = array_ops.shape(x)[new Slice(rows, cols + 1)];
            new_shape *= constant_op.constant(np.array(height_factor, width_factor));

            if (data_format == "channels_first")
                // x = permute_dimensions(x, [0, 2, 3, 1]);
                throw new NotImplementedException("");
            if (interpolation == "nearest")
                x = tf.image.resize_images_v2(x, new_shape, method: ResizeMethod.NEAREST_NEIGHBOR);

            if (data_format == "channels_first")
                // x = permute_dimensions(x, [0, 3, 1, 2]);
                throw new NotImplementedException("");

            int new_height = original_shape[rows] < 0 ? -1 : (int)original_shape[rows] * height_factor;
            int new_width = original_shape[cols] < 0 ? -1 : (int)original_shape[cols] * width_factor;

            Shape output_shape = data_format == "channels_first" ?
                (-1, -1, new_height, new_width) : (-1, new_height, new_width, -1);
            x.shape = output_shape;
            return x;
        }

        /// <summary>
        /// Concatenates a list of tensors alongside the specified axis.
        /// </summary>
        /// <param name="tensors">list of tensors to concatenate.</param>
        /// <param name="axis">concatenation axis.</param>
        /// <returns></returns>
        public Tensor concatenate(Tensors tensors, int axis = -1)
        {
            if (axis < 0)
            {
                var rank = tensors[0].ndim;
                if (rank > -1)
                    axis += rank;
                else
                    axis = 0;
            }

            return array_ops.concat(tensors, axis);
        }

        public Tensor conv2d_transpose(Tensor x,
                     IVariableV1 kernel,
                     Tensor output_shape,
                     Shape strides = null,
                     string padding = "valid",
                     string data_format = null,
                     Shape dilation_rate = null)
        {
            /*
            var force_transpose = false;
            if (data_format == "channels_first" && !dilation_rate.Equals(new[] { 1, 1 }))
                force_transpose = true;
            x, tf_data_format = _preprocess_conv2d_input(x, data_format, force_transpose)
            */
            var tf_data_format = "NHWC";
            padding = padding.ToUpper();
            strides = new Shape(1, strides[0], strides[1], 1);
            if (dilation_rate.Equals(new long[] { 1, 1 }))
                x = nn_impl.conv2d_transpose(x, kernel, output_shape, strides,
                    padding: padding,
                    data_format: tf_data_format);
            else
                throw new NotImplementedException("dilation_rate other than [1,1] is not yet supported");

            return x;
        }

        public static (Tensors, Tensors) convert_inputs_if_ragged(OneOf<Tensor, RaggedTensor> inputs)
        {
            throw new NotImplementedException();
        }

        // 
        public static (Tensors, Tensors, Tensors) rnn(
            Func<Tensors, Tensors, (Tensors, Tensors)> step_function, // args:inputs, states, return:output, new_states
            Tensors inputs, // inputs is a tuple of tensors (one per input sequence)
            Tensors initial_states,
            bool go_backwards = false,
            Tensor? mask = null,
            Tensors? constants = null,
            bool unroll = false,
            Tensors? input_length = null, // An integer or a 1-D Tensor,depending on whether the time dimension is fixed-length or not
            bool time_major = false,
            bool zero_output_for_mask = false,
            bool return_all_outputs = true)
        {

            Tensors swap_batch_timestep(Tensors input_t)
            {
                var axes = Enumerable.Range(0, input_t.rank).ToArray();
                axes[0] = 1;
                axes[1] = 0;
                return tf.transpose(input_t, axes);
            }

            if (!time_major)
            {
                inputs = nest.map_structure(swap_batch_timestep, inputs);
            }

            var flatted_inptus = nest.flatten(inputs);
            var time_steps = flatted_inptus[0].shape[0];
            var batch = flatted_inptus[0].shape[1];
            var time_step_t = tf.shape(flatted_inptus[0])[0];

            foreach (var input_ in flatted_inptus)
            {
                input_.shape.with_rank_at_least(3);
            }

            if (mask != null)
            {
                if (mask.dtype != TF_DataType.TF_BOOL)
                {
                    mask = tf.cast(mask, TF_DataType.TF_BOOL);
                }

                if (mask.rank == 2)
                {
                    mask = tf.expand_dims(mask, -1);
                }

                if (!time_major)
                {
                    mask = swap_batch_timestep(mask);
                }

            }

            if (constants == null)
            {
                constants = new List<Tensor>();
            }

            // tf.where needs its condition tensor to be the same shape as its two
            // result tensors, but in our case the condition (mask) tensor is
            // (nsamples, 1), and inputs are (nsamples, ndimensions) or even more.
            // So we need to broadcast the mask to match the shape of inputs.
            // That's what the tile call does, it just repeats the mask along its
            // second dimension n times.

            Tensors _expand_mask(Tensors mask_t, Tensors input_t, int fixed_dim = 1)
            {
                if (nest.is_nested(mask_t))
                {
                    throw new ValueError($"mask_t is expected to be tensor, but got {mask_t}");
                }

                if (nest.is_nested(input_t))
                {
                    throw new ValueError($"input_t is expected to be tensor, but got {input_t}");
                }

                var rank_diff = input_t.rank - mask_t.rank;
                for (int i = 0; i < rank_diff; i++)
                {
                    mask_t = tf.expand_dims(mask_t, -1);
                }
                var multiples = Enumerable.Repeat(1, fixed_dim).ToArray().concat(input_t.shape.as_int_list().ToList().GetRange(fixed_dim, input_t.rank));
                return tf.tile(mask_t, multiples);
            }

            Tensors outputs = new Tensors();
            Tensors output_time_zero = new Tensors();
            Tensors last_output = new Tensors();
            Tensors new_states = new Tensors();
            if (unroll)
            {
                if (time_steps == 0)
                {
                    throw new ValueError("Unrolling requires a fixed number of timesteps.");
                }

                // Process the input tensors. The input tensor need to be split on the
                // time_step dim, and reverse if go_backwards is True. In the case of
                // nested input, the input is flattened and then transformed
                // individually.  The result of this will be a tuple of lists, each of
                // the item in tuple is list of the tensor with shape (batch, feature)


                // TODO(Wanglongzhi2001)，step_func接受的第二个参数为List，但是最后却用的tuple
                //var states = Tuple.Create(initial_states);
                var states = initial_states;

                var successive_states = new Tensors();
                var successive_outputs = new Tensors();

                // Process the input tensors. The input tensor need to be split on the
                // time_step dim, and reverse if go_backwards is True. In the case of
                // nested input, the input is flattened and then transformed
                // individually.  The result of this will be a tuple of lists, each of
                // the item in tuple is list of the tensor with shape (batch, feature)




                Tensors _process_single_input_t(Tensors input_t)
                {
                    input_t = tf.unstack(input_t); // unstack for time_step dim
                    if (go_backwards)
                    {
                        input_t.Reverse();
                    }
                    return input_t;
                }

                // TODO(Wanglongzhi2001)
                Tensors processed_input;
                if (nest.is_nested(inputs))
                {
                    processed_input = nest.map_structure(_process_single_input_t, inputs);
                }
                else
                {
                    processed_input = _process_single_input_t(inputs);
                }

                object _get_input_tensor(int time)
                {
                    List<Tensor> inp = new List<Tensor>();
                    foreach (var t_ in processed_input)
                    {
                        inp.Add(t_[time]);
                    }
                    return nest.pack_sequence_as(inputs, inp);
                }

                if (mask != null)
                {
                    var mask_list = tf.unstack(mask);
                    if (go_backwards)
                    {
                        mask_list.Reverse();
                    }

                    for (int i = 0; i < time_steps; i++)
                    {
                        // TODO(Wanglongzhi2001),deal with _get_input_tensor
                        var inp = _get_input_tensor(i);
                        var mask_t = mask_list[i];
                        // TODO
                        var (output, newStates) = step_function((Tensors)inp, new Tensors { states, constants });

                        var tiled_mask_t = _expand_mask(mask_t, output);

                        Tensors prev_output;
                        if (successive_outputs == null)
                        {
                            prev_output = tf.zeros_like(output);
                        }
                        else
                        {
                            prev_output = successive_outputs[successive_outputs.Length - 1];
                        }

                        output = tf.where(tiled_mask_t, output, prev_output);

                        //var flat_states = nest.flatten(states);
                        //var flat_new_states = nest.flatten(newStates);
                        var flat_states = states.ToList();
                        var flat_new_states = newStates.ToList();

                        var tiledMaskT = flat_states
                            .Select(s => _expand_mask(mask_t, s))
                            .ToArray();
                        var tuple = Tuple.Create(tiledMaskT);

                        List<Tensor> flat_final_states = new List<Tensor>();
                        foreach (var (m, s, ps) in Enumerable.Zip(tiled_mask_t, flat_new_states, flat_states))
                        {
                            flat_final_states.Add(tf.where(m, s, ps));
                        }

                        states = (Tensors)nest.pack_sequence_as(states, flat_final_states);
                        if (return_all_outputs)
                        {
                            successive_outputs.Add(output);
                            successive_states.Add(states);
                        }
                        else
                        {
                            successive_outputs = new Tensors { output };
                            successive_states = new Tensors { states };
                        }

                    }
                    last_output = successive_outputs[successive_outputs.Length - 1];
                    new_states = successive_states[successive_states.Length - 1];
                    outputs = tf.stack(successive_outputs);

                    if (zero_output_for_mask)
                    {
                        last_output = tf.where(_expand_mask(mask_list[mask_list.Length - 1], last_output), last_output, tf.zeros_like(last_output));
                        outputs = tf.where(_expand_mask(mask, outputs, fixed_dim: 2), outputs, tf.zeros_like(outputs));
                    }
                    else // mask is null
                    {
                        for (int i = 0; i < time_steps; i++)
                        {
                            var inp = _get_input_tensor(i);
                            var (output, newStates) = step_function((Tensors)inp, new Tensors { states, constants });
                            states = newStates;

                            if (return_all_outputs)
                            {
                                successive_outputs.Add(output);
                                successive_states.Add(newStates);
                            }
                            else
                            {
                                successive_outputs = new Tensors { output };
                                successive_states = new Tensors { newStates };
                            }
                        }
                        last_output = successive_outputs[successive_outputs.Length - 1];
                        new_states = successive_states[successive_states.Length - 1];
                        outputs = tf.stack(successive_outputs);
                    }
                }
            }
            else // unroll == false
            {
                var states = initial_states;
                //  Create input tensor array, if the inputs is nested tensors, then it
                //  will be flattened first, and tensor array will be created one per
                //  flattened tensor.
                var input_ta = new List<TensorArray>();
                for (int i = 0; i < flatted_inptus.Count; i++)
                {
                    input_ta.Add(tf.TensorArray(dtype: flatted_inptus[i].dtype, size: time_step_t));
                }

                // Get the time(0) input and compute the output for that, the output will
                // be used to determine the dtype of output tensor array. Don't read from
                // input_ta due to TensorArray clear_after_read default to True.
                var inps = new Tensors();
                foreach (var inp in flatted_inptus)
                {
                    inps.Add(inp[0]);
                }
                var input_time_zero = nest.pack_sequence_as(inputs, inps);

                // output_time_zero is used to determine the cell output shape and its
                // dtype.  the value is discarded.
                (output_time_zero, _) = step_function((Tensor)input_time_zero, new Tensors { initial_states, constants });

                var output_ta_size = return_all_outputs ? time_step_t : tf.constant(1);
                var output_ta = new List<TensorArray>();
                for (int i = 0; i < output_time_zero.ToList().Count; i++)
                {
                    var Out = output_time_zero.ToList()[i];
                    output_ta.Add(tf.TensorArray(dtype: Out.dtype, size: output_ta_size, element_shape: Out.shape));
                }

                var time = tf.constant(0, dtype: TF_DataType.TF_INT32, name: "time");



                Func<Tensor, Tensor>? masking_fn;
                Func<Tensors, Tensors, Tensors, Tensors>? compute_masked_output = null;
                if (mask != null)
                {
                    if (go_backwards)
                    {
                        mask = tf.reverse(mask, axis: new[] { 0 });
                    }
                    var mask_ta = tf.TensorArray(dtype: TF_DataType.TF_BOOL, size: time_step_t);
                    mask_ta = mask_ta.unstack(mask);

                    masking_fn = (time) =>
                    {
                        return mask_ta.read(time);
                    };

                    compute_masked_output = (mask_t, flat_out, flat_mask) =>
                    {
                        var tiled_mask_t = new Tensors();
                        foreach (var o in flat_out)
                        {
                            tiled_mask_t.Add(_expand_mask(mask_t, o, fixed_dim: mask_t.rank));
                        }

                        Tensors res = new Tensors();
                        foreach (var (m, o, fm) in Enumerable.Zip(tiled_mask_t, flat_out, flat_mask))
                        {
                            res.Add(tf.where(m, o, fm));
                        }
                        return res;
                    };
                }
                // TODO(Wanglongzhi2001), what the input_length's type should be(an integer or a single tensor)?
                else if (input_length is Tensor)
                {
                    if (go_backwards)
                    {
                        var max_len = tf.reduce_max(input_length, axis: 0);
                        var rev_input_length = tf.subtract(max_len - 1, input_length);

                        masking_fn = (time) =>
                        {
                            return tf.less(rev_input_length, time);
                        };
                    }
                    else
                    {
                        masking_fn = (time) =>
                        {
                            return tf.greater(input_length, time);
                        };
                    }

                    compute_masked_output = (mask_t, flat_out, flat_mask) =>
                    {
                        var res = new List<Tensor>();
                        foreach (var (o, zo) in zip(flat_out, flat_mask))
                        {
                            res.Add(tf.where(mask_t, o, zo));
                        }
                        return res;
                    };
                }
                else
                {
                    masking_fn = null;
                }


                if (masking_fn != null)
                {
                    // Mask for the T output will be base on the output of T - 1. In the
                    // case T = 0, a zero filled tensor will be used.
                    var flat_zero_output = new Tensors();
                    foreach (var o in nest.flatten(output_time_zero))
                    {
                        flat_zero_output.Add(tf.zeros_like(o));
                    }


                    (Tensor, List<TensorArray>, Tensors, Tensors) _step(Tensor time, List<TensorArray> output_ta_t, Tensors prev_output, Tensors states)
                    {
                        /*
                         RNN step function.
                         Args:
                            time: Current timestep value.
                            output_ta_t: TensorArray.
                            prev_output: tuple of outputs from time - 1.
                            *states: List of states.
                         Returns:
                            Tuple(todo): `(time + 1, output_ta_t, output) + tuple(new_states)`                          
                         */

                        var current_input = input_ta.Select(x => x.read(time)).ToList();
                        // maybe set shape
                        // TODO(Wanglongzhi2001),deal with nest.pack_sequence_as's return type
                        current_input = (List<Tensor>)nest.pack_sequence_as(inputs, current_input);
                        var mask_t = masking_fn(time);
                        var (output, new_states) = step_function(current_input, new Tensors { states, constants });
                        // mask output
                        //var flat_output = nest.flatten(output);
                        var flat_output = output.ToList();

                        var flat_mask_output = zero_output_for_mask ? flat_zero_output : prev_output.ToList();

                        // TODO(Wanglongzhi2001),deal with compute_masked_output's third parameter's type
                        var flat_new_output = compute_masked_output(mask_t, flat_output, flat_mask_output);

                        // mask states
                        var flat_state = states.ToList();
                        var flat_new_state = new_states.ToList();

                        foreach (var (state, new_state) in zip(flat_state, flat_new_state))
                        {
                            if (new_state is Tensor)
                            {
                                new_state.set_shape(state.shape);
                            }
                        }

                        var flat_final_state = compute_masked_output(mask_t, flat_new_state, flat_state);
                        new_states = (Tensors)nest.pack_sequence_as(new_states, flat_final_state);

                        var ta_index_to_write = return_all_outputs ? time : tf.constant(0);
                        var Output_ta_t = new List<TensorArray>();
                        // TODO(Wanglongzhi2001),deal with zip output_ta_t
                        foreach (var (ta, Out) in zip(output_ta_t, flat_new_output))
                        {
                            Output_ta_t.Add(ta.write(ta_index_to_write, Out));
                        }



                        //new_states = (Tensors)nest.pack_sequence_as(initial_states, flat_new_state);


                        return (time + 1, Output_ta_t, flat_new_output, new_states);

                    }
                    Func<Tensor, Tensor> cond = (time) => (time < time_step_t);

                    var final_outputs = tf.while_loop(cond: cond, body: _step, loop_vars: (time, output_ta, flat_zero_output, states));
                    new_states = final_outputs.Item4;
                    output_ta = final_outputs.Item2;

                }
                else
                {
                    (Tensor, List<TensorArray>, Tensors) _step(Tensor time, List<TensorArray> output_ta_t, Tensors states)
                    {
                        var current_input = input_ta.Select(x => x.read(time)).ToList();
                        // maybe set shape
                        // TODO(Wanglongzhi2001),deal with nest.pack_sequence_as's return type
                        current_input = (List<Tensor>)nest.pack_sequence_as(inputs, current_input);
                        var (output, new_states) = step_function(current_input, new Tensors { states, constants });
                        var flat_state = states.ToList();
                        var flat_new_state = new_states.ToList();
                        foreach (var (state, new_state) in zip(flat_state, flat_new_state))
                        {
                            if (new_state is Tensor)
                            {
                                new_state.set_shape(state.shape);
                            }
                        }
                        var flat_output = output.ToList();
                        var ta_index_to_write = return_all_outputs ? time : tf.constant(0);
                        var Output_ta_t = new List<TensorArray>();
                        foreach (var (ta, out_) in zip(output_ta_t, flat_output))
                        {
                            Output_ta_t.Add(ta.write(ta_index_to_write, out_));
                        }

                        new_states = (Tensors)nest.pack_sequence_as(initial_states, flat_new_state);
                        return (time + 1, Output_ta_t, new_states);
                    }
                    Func<Tensor, Tensor> cond = (time) => (time < time_step_t);
                    var final_outputs = tf.while_loop(cond: cond, body: _step, loop_vars: (time, output_ta, states));
                    new_states = final_outputs.Item3;
                    output_ta = final_outputs.Item2;

                }
                //Tensors outputs = new Tensors();
                foreach (var o in output_ta)
                {
                    outputs.Add(o.stack());
                }
                foreach (var o in outputs)
                {
                    last_output.Add(o[-1]);
                }
                outputs = (Tensors)nest.pack_sequence_as(output_time_zero, outputs);
                last_output = (Tensors)nest.pack_sequence_as(output_time_zero, last_output);

            }

            Func<Tensor, Tensor> set_shape;
            set_shape = (output_) =>
            {
                if (output_ is Tensor)
                {
                    var shape = output_.shape.as_int_list();
                    if (return_all_outputs)
                    {
                        shape[0] = (int)time_steps;
                    }
                    else
                    {
                        shape[0] = 1;
                    }
                    shape[1] = (int)batch;
                    output_.set_shape(new Tensor(shape));
                }
                return output_;
            };

            var Outputs = (Tensors)nest.map_structure(set_shape, outputs);
            if (!time_major)
            {
                Outputs = nest.map_structure(swap_batch_timestep, outputs);
            }
            return (last_output, Outputs, new_states);

        }
    }
}
