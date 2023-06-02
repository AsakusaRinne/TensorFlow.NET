﻿using System;
using System.Collections.Generic;
using System.Text;
using Tensorflow.Keras.ArgsDefinition;
using Tensorflow.Keras.Engine;
using Tensorflow.Keras.Saving;
using static Tensorflow.Binding;

namespace Tensorflow.Keras.Layers {
    public class Exponential : Layer
    {
        public Exponential(LayerArgs args) : base(args)
        {
            // Exponential has no args
        }
        public override void build(KerasShapesWrapper input_shape)
        {
            base.build(input_shape);
        }
        protected override Tensors Call(Tensors inputs, Tensor mask = null, bool? training = null, Tensors initial_state = null, Tensors constants = null)
        {
            Tensor output = inputs;
            return tf.exp(output);
        }
        public override Shape ComputeOutputShape(Shape input_shape)
        {
            return input_shape;
        }
    }
}
