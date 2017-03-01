﻿using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinCoinOp: GremlinTranslationOperator
    {
        public double Probability { get; set; }

        public GremlinCoinOp(double probability)
        {
            Probability = probability;
        }

        internal override GremlinToSqlContext GetContext()
        {
            GremlinToSqlContext inputContext = GetInputContext();
            
            inputContext.PivotVariable.Coin(inputContext, Probability);

            return inputContext;
        }
    }
}
