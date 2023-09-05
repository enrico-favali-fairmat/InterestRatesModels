/* Copyright (C) 2009-2012 Fairmat SRL (info@fairmat.com, http://www.fairmat.com/)
 * Author(s): Matteo Tesser (matteo.tesser@fairmat.com)
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */


#define TFORWARDFORMULATION //uses HW formulation by Brigo which does not contains second derivatives

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;
using DVPLDOM;
using DVPLI;
using Mono.Addins;

namespace HullAndWhiteOneFactor
{
    /// <summary>
    /// Implementation of one factor Hull and White model.
    /// </summary>
    [Serializable]
    public class HW1 : IExtensibleProcessIR, IZeroRateReference, IMarkovSimulator/*IFullSimulator*/,
                       IParsable, IEstimationResultPopulable, IGreeksDerivativesInfo,
                       IOpenCLCode, IPostSimulationTransformation, IExportableContainer
    {
        #region SerializedFields

        /// <summary>
        /// Reference to the zero rate.
        /// </summary>
        private IModelParameter zrReference;

        /// <summary>
        /// Rate of mean reversion.
        /// </summary>
        protected IModelParameter alpha1;

        /// <summary>
        /// Standard deviation.
        /// </summary>
        protected IModelParameter sigma1;

        /// <summary>
        /// RMSE Abs.
        /// </summary>
        protected IModelParameter rmse_a;

        /// <summary>
        /// RMSE Rel.
        /// </summary>
        protected IModelParameter rmse_r;

        /// <summary>
        /// Optimization problem Upper Bounds.
        /// </summary>
        protected IModelParameter upperBounds;

        /// <summary>
        /// Optimization problem Lower Bounds.
        /// </summary>
        protected IModelParameter lowerBounds;

        /// <summary>
        /// Market price of risk.
        /// </summary>
        [OptionalField(VersionAdded = 3)]
        protected IModelParameter lambda0;

        /// <summary>
        /// Drift adjustment: can be used for adding risk premium or quanto adjustments.
        /// </summary>
        [OptionalField(VersionAdded = 2)]
        private IModelParameter driftAdjustment;

        #endregion

        #region Properties

        /// <summary>
        /// Gets or Sets the reference to the zero rate.
        /// Used to expose to the <see cref="ExternalSymbolReference"/> API.
        /// </summary>
        [ExternalSymbolReference("ZR", typeof(PFunction))]
        public IModelParameter ZRReference
        {
            get
            {
                return zrReference;
            }

            set
            {
                zrReference = value;
            }
        }


        public IModelParameter Alpha1
        {
            get { return alpha1;}
            set { alpha1 = value; }
        }

        #endregion Properties

        /// <summary>
        /// Temporary zero rate function, used to optimize the simulation.
        /// </summary>
        [NonSerialized]
        protected Function zeroRateCurve;

        /// <summary>
        /// Temporary value for the Rate of mean reversion, used to optimize the simulation.
        /// </summary>
        [NonSerialized]
        protected double alpha1Temp;

        /// <summary>
        /// Temporary value for the standard deviation, used to optimize the simulation.
        /// </summary>
        [NonSerialized]
        protected double sigma1Temp;

        /// <summary>
        /// Keeps the readable description of the alpha model variable.
        /// </summary>
        private const string alphaDescription = "Alpha";

        /// <summary>
        /// Keeps the readable description of the sigma model variable.
        /// </summary>
        private const string sigmaDescription = "Sigma";

        /// <summary>
        /// Keeps the readable description of the zero rate.
        /// </summary>
        private const string zeroRateDescription = "Zero Rate";

        /// <summary>
        /// Keeps the readable description of the lambda0 model variable.
        /// </summary>
        private static string lambda0Description = "Lambda";

//#if TFORWARDFORMULATION
        /// <summary>
        /// Temporary variable for the transformation (mDailyDates).
        /// </summary>
        [NonSerialized]
        protected double[] alphaT;
//#else
        [NonSerialized]
        protected double[] thetaT;
//#endif
        /// <summary>
        /// Keeps the readable description for drift adjustment.
        /// </summary>
        private const string driftAdjustmentDescription = "Drift Adjustment";

        /// <summary>
        /// Set a lower bound for alpha. Smaller values doe not make sense.
        /// </summary>
        internal static double alphaLowerBound = 0.0001;


        /// <summary>
        /// Initializes a new instance of the HW1 class with
        /// alpha 0.1, sigma 0.001 and an empty zeroRateReference.
        /// </summary>
        public HW1()
            : this(0.1, 0.001, 0.0, string.Empty)
        {
        }

        /// <summary>
        /// Initializes a new instance of the HW1 class given alpha, sigma, lambda and a zero rate reference.
        /// </summary>
        /// <param name="alpha">The rate of the mean reversion to be used to initialize HW.</param>
        /// <param name="sigma">The standard deviation to be used to initialize HW.</param>
        /// <param name="lambda">The market price of risk to be used to initialize HW.</param>
        /// <param name="zeroRateReference">
        /// Reference to the zero rate to be used to initialize HW.
        /// </param>
        public HW1(double alpha, double sigma, double lambda, string zeroRateReference)
        {
            this.alpha1 = new ModelParameter(alpha, alphaDescription);
            this.sigma1 = new ModelParameter(sigma, sigmaDescription);
            this.lambda0 = new ModelParameter(lambda, lambda0Description);
            this.zrReference = new ModelParameter(zeroRateReference, zeroRateDescription);
        }

        /// <summary>
        /// Initializes a new instance of the HW1 class given alpha, sigma and a zero rate reference.
        /// </summary>
        /// <param name="alpha">The rate of the mean reversion to be used to initialize HW.</param>
        /// <param name="sigma">The standard deviation to be used to initialize HW.</param>
        /// <param name="zeroRateReference">
        /// Reference to the zero rate to be used to initialize HW.
        /// </param>
        public HW1(double alpha, double sigma, string zeroRateReference)
            : this(alpha, sigma, 0.0, zeroRateReference)
        {
        }

        /// <summary>
        /// Initializes optional fields after deserialization.
        /// </summary>
        /// <param name='context'>
        /// The parameter is not used.
        /// </param>
        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            if (this.driftAdjustment == null)
                this.driftAdjustment = new ModelParameter(0, driftAdjustmentDescription);
            if (this.lambda0 == null)
                this.lambda0= new ModelParameter(0, lambda0Description);
        }

        #region IParsable Members

        /// <summary>
        /// Ensure the parameters are correct.
        /// </summary>
        /// <param name='p_Context'>
        /// The underlying project.
        /// </param>
        /// <returns>
        /// False if there were no parse errors.
        /// </returns>
        public virtual bool Parse(IProject p_Context)
        {
            bool errors = false;
            BoolHelper.AddBool(errors, this.alpha1.Parse(p_Context));
            BoolHelper.AddBool(errors, this.sigma1.Parse(p_Context));
            if (this.zrReference.Expression.IndexOf("@") == -1)
            {
                p_Context.AddError(this.zrReference.Expression +
                                   " is not a reference to a zero rate curve");
            }

            object zrref = Engine.Parser.EvaluateAsReference(this.zrReference.Expression);
            if (!Engine.Parser.GetParserError())
            {
                this.zeroRateCurve = zrref as Function;
                if (this.zeroRateCurve == null)
                {
                    errors = true;

                    p_Context.AddError("Cannot find the Zero Rate Curve! " +
                                       this.zrReference.Expression);
                }
            }
            else
                errors = true;

            if (!errors)
            {
                this.alpha1Temp = this.alpha1.fV();
                this.sigma1Temp = this.sigma1.fV();
            }

            return errors;
        }

        #endregion

        #region IZeroRateReference Members

        /// <summary>
        /// Associate the process to a zero rate defined in the Fairmat model
        /// (e.g. @zr1).
        /// </summary>
        /// <param name='zr'>
        /// The zero rate reference.
        /// </param>
        public void SetZeroRateReference(string zr)
        {
            this.zrReference = new ModelParameter(zr, "Zero Rate");
        }

        /// <summary>
        /// Gets the zero rate reference.
        /// </summary>
        /// <returns>
        /// The zero rate reference.
        /// </returns>
        public string GetZeroRateReference()
        {
            return this.zrReference.Expression;
        }
        #endregion

        #region IExtensibleProcessIR Members

        /// <summary>
        /// Calculates the value of a Bond under the Hull and White model.
        /// </summary>
        /// <param name='dynamic'>
        /// The simulated process.
        /// </param>
        /// <param name='dates'>
        /// The vector of reference dates.
        /// </param>
        /// <param name='i'>
        /// The index at which the state variable must be sampled.
        /// </param>
        /// <param name='t'>
        /// The date in years/fractions at at which the state variable must be sampled.
        /// </param>
        /// <param name='T'>
        /// The maturity of the bond.
        /// </param>
        /// <returns>The value of the bound at index i using the HW model.</returns>
        public virtual double Bond(IReadOnlyMatrixSlice dynamic, double[] dates, int i, double t, double T)
        {
#if TFORWARDFORMULATION
            double y = dynamic[i, 0] - this.alphaT[i];
            return Math.Exp(A(t, T, this.alpha1Temp, this.sigma1Temp, this.zeroRateCurve) - y * B(T - t, this.alpha1Temp));
#else
            throw new NotImplementedException();
#endif
        }

        #endregion

        #region IExtensibleProcess Members

        /// <summary>
        /// Gets a value indicating whether FullSimulation is implemented, in this
        /// case it doesn't so it always returns false.
        /// </summary>
        public bool ImplementsFullSimulation
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether a Markov based simulation is implemented, in this
        /// case it does so it always returns true.
        /// </summary>
        public bool ImplementsMarkovBasedSimulation
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// Gets the ProcessInfo for this plugin, in this case H&W1.
        /// </summary>
        public virtual ProcessInfo ProcessInfo
        {
            get
            {
                return new ProcessInfo("H&W1");
            }
        }

        /// <summary>
        /// Gets the information required in order to allow the simulation to run.
        /// Hull And White one factor has:
        /// * 0 latent components in the state components.
        /// * 1 component of noise.
        /// * 1 state component.
        /// * The unique component is named short rate.
        /// </summary>
        public SimulationInfo SimulationInfo
        {
            get
            {
                SimulationInfo s = new SimulationInfo();
                s.LatentSize = 0;
                s.NoiseSize = 1;
                s.StateDescription = new string[] { "short rate" };
                s.StateSize = 1;
                return s;
            }
        }

        /// <summary>
        /// Called by Simulator after parse.
        /// Initializes here time-dependant but not state dependent variables.
        /// </summary>
        /// <param name='dates'>
        /// The dates at which the process realizations will be requested.
        /// </param>
        public virtual void Setup(double[] dates)
        {
            //Console.WriteLine("Expected short rate");
            double dt = 0;
#if TFORWARDFORMULATION
            this.alphaT = new double[dates.Length];
#else
            this.thetaT = new double[dates.Length];
#endif
            for (int i = 0; i < dates.Length; i++)
            {
                if (i < dates.Length - 1)
                    dt = dates[i + 1] - dates[i];
#if TFORWARDFORMULATION
                this.alphaT[i] = this.alphaTFunc(dates[i],dt);
#else
                this.thetaT[i] = theta(dates[i], dt);
#endif
                //Console.WriteLine(dates[i] + "\t" + ExpectedShortRate(dates[i]));
            }
            /*
            Console.WriteLine("avg");
            Console.WriteLine(dates[dates.Length - 1] +"\t"+ ExpectedAverageRate(dates[dates.Length - 1]));
            Console.WriteLine("alternative sim");
            HWCompactSimulator hwf = new HWCompactSimulator() { a = alpha1Temp, sigma = sigma1Temp, zr = this.zeroRateCurve };
            List<double> simDates,fR,avgR;
            hwf.Simulate(dates[dates.Length - 1], out simDates, out fR, out avgR);
            Console.WriteLine(fR + "\t" + avgR);
            */
        }

        protected virtual double theta(double t,  double dt)
        {
            return Ft(t, dt) + alpha1Temp * F(t, dt) + (1.0 - Math.Exp(-2.0 * alpha1Temp * t)) * sigma1Temp * sigma1Temp / (2.0 * alpha1Temp);
        }


        #endregion

        /// <summary>
        /// Calculates the value of alpha function at time t
        /// </summary>
        /// <param name="t">Time at which calculate alpha</param>
        /// <returns>Alpha function value</returns>
        protected virtual double alphaTFunc(double t,double dt)
        {
            //double dt = 0.001;
            return this.F(t, dt) + this.sigma1Temp * this.sigma1Temp * Math.Pow(1.0 - Math.Exp(-this.alpha1Temp * t), 2.0) / (2.0 * this.alpha1Temp * this.alpha1Temp);
        }

        #region IExportableContainer Members

        /// <summary>
        /// Creates a list of all the sub-objects that can be edited.
        /// </summary>
        /// <param name="recursive">
        /// The parameter is not used.
        /// </param>
        /// <returns>
        /// The created list with all the sub objects that can be edited.
        /// </returns>
        public virtual List<IExportable> ExportObjects(bool recursive)
        {
            List<IExportable> parameters = new List<IExportable>();
            parameters.Add(this.alpha1);
            parameters.Add(this.sigma1);
            parameters.Add(this.lambda0);
            parameters.Add(this.zrReference);
            parameters.Add(this.rmse_a);
            parameters.Add(this.rmse_r);

            return parameters;
        }

        #endregion

        #region IMarkovSimulator Members

        /// <summary>
        /// Gets details about the structure of the functions A and B of the Markov
        /// process.
        /// In this case drift is state dependant and not time dependant and
        /// volatility is neither state or time dependent.
        /// </summary>
        public DynamicInfo DynamicInfo
        {
            get
            {
                return new DynamicInfo(false, true, false, false);
            }
        }

        /// <summary>
        /// Gets the starting point for the process.
        /// </summary>
        public virtual double[] x0
        {
            get
            {
                return new double[] { 0 };
            }
        }

        /// <summary>
        /// This function defines the drift in the HW Markov process.
        /// The formula to calculate the A component is
        /// A = theta(t) - alpha * previous State.
        /// </summary>
        /// <param name="i">The time step of the simulation.</param>
        /// <param name="x">The state vector at the previous state.</param>
        /// <param name="a">The output of the function.</param>
        public unsafe void a(int i, double* x, double* a)
        {
            a[0] = -this.alpha1Temp * x[0] + this.lambda0.fV() * this.sigma1Temp;
        }

        /// <summary>
        /// This function defines the volatility in the HW Markov process.
        /// The formula to calculate the B component is
        /// B = sigma.
        /// </summary>
        /// <param name="i">The parameter is not used.</param>
        /// <param name="x">The parameter is not used.</param>
        /// <param name="b">The output of the function.</param>
        public unsafe void b(int i, double* x, double* b)
        {
            b[0] = this.sigma1Temp;
        }

        /// <summary>
        /// This function defines drift and the volatility in the HW Markov process.
        /// The formula to calculate the B component is
        /// A = theta(t) - alpha * previous State.
        /// B = sigma.
        /// </summary>
        /// <param name="i">The time step of the simulation.</param>
        /// <param name="x">The state vector at the previous state.</param>
        /// <param name="a">The output state dependent drift.</param>
        /// <param name="b">The output volatility.</param>
        public unsafe virtual void ab(int i, double* x, double* a,double*b)
        {
#if TFORWARDFORMULATION
            a[0] = -this.alpha1Temp * x[0] + this.lambda0.fV() * this.sigma1Temp;
#else
            a[0] = thetaT[i] -this.alpha1Temp  * x[0]   + this.lambda0.fV() * this.sigma1Temp;
#endif
            b[0] = this.sigma1Temp;
        }


        /// <summary>
        /// Sets the passed array with a Boolean stating if the process
        /// must be simulated as a log-normal process.
        /// </summary>
        /// <param name="isLog">
        /// A reference to the array to be set with the required information.
        /// </param>
        public void isLog(ref bool[] isLog)
        {
            isLog[0] = false;
        }

        #endregion

        /// <summary>
        /// Helper function to make functions easier to read.
        /// Just returns the value of the zero rate at position t.
        /// </summary>
        /// <param name="t">The position where to get the value of the zero rate from.</param>
        /// <returns>The value of the zero rate at position t.</returns>
        private double ZR(double t)
        {
            return this.zeroRateCurve.Evaluate(t);
        }

        /// <summary>
        /// Numerically calculates the instantaneous forward rate.
        /// </summary>
        /// <param name='t'>
        /// Time at which calculate the forward rate.
        /// </param>
        /// <param name='dt'>
        /// Interval to be used in the numerical derivative.
        /// </param>
        /// <returns>
        /// The value of the instantaneous forward rate.
        /// </returns>
        protected double F(double t, double dt)
        {
            double zrT=ZR(t);
            return t * (ZR(t + dt) - zrT) / dt + zrT;
        }
        /// <summary>
        /// First derivative w.r.t time of forward rate
        /// </summary>
        /// <param name="t"></param>
        /// <param name="dt"></param>
        /// <returns></returns>
        protected double Ft(double t, double dt)
        {
            return (F(t + dt, dt) - F(t - dt, dt)) / (2 * dt);
        }
        

        /// <summary>
        /// Calculates the function A() to be used in the Bond() method.
        /// </summary>
        /// <param name='t'>
        /// The time at which the Bond price will be calculated.
        /// </param>
        /// <param name='T'>
        /// The bond maturity.
        /// </param>
        /// <param name='alpha'>
        /// Hull-White alpha parameter.
        /// </param>
        /// <param name='sigma'>
        /// Hull-White sigma parameter.
        /// </param>
        /// <param name='zeroRateCurve'>
        /// Zero rate curve.
        /// </param>
        /// <returns>
        /// A double with the value of the A() function.
        /// </returns>
        private static double A(double t, double T, double alpha, double sigma, Function zeroRateCurve)
        {
            double dT = T - t;
            double firstTerm = sigma * sigma * (alpha * dT - 2.0 * (1.0 - Math.Exp(-alpha * dT))
                + 0.5 * (1.0 - Math.Exp(-2.0 * alpha * dT))) / (2.0 * Math.Pow(alpha, 3.0));

            return firstTerm - AlphaInt(t, T, alpha, sigma, zeroRateCurve);
        }

        /// <summary>
        /// Calculates the integral of alpha function to be used in the A() method.
        /// </summary>
        /// <param name="t">Lower value defining integration interval.</param>
        /// <param name="T">Upper value defining integration interval.</param>
        /// <param name="alpha">Hull-White alpha parameter.</param>
        /// <param name="sigma">Hull-White sigma parameter.</param>
        /// <param name="zeroRateCurve">Zero rate curve.</param>
        /// <returns>The integral of alpha function between t and T.</returns>
        private static double AlphaInt(double t, double T, double alpha, double sigma, Function zeroRateCurve)
        {
            double firstTerm = zeroRateCurve.Evaluate(T) * T - zeroRateCurve.Evaluate(t) * t;
            return firstTerm + sigma * sigma * (alpha * (T - t) - 2.0 * (Math.Exp(-alpha * t) - Math.Exp(-alpha * T))
                + 0.5 * (Math.Exp(-2.0 * alpha * t) - Math.Exp(-2.0 * alpha * T))) / (2.0 * Math.Pow(alpha, 3.0));
        }

        /// <summary>
        /// Calculates the function B() to be used in the Bond() method.
        /// </summary>
        /// <param name='T'>
        /// The difference between bond maturity time and valuation time.
        /// </param>
        /// <param name='alpha'>
        /// Hull-White alpha parameter.
        /// </param>
        /// <returns>
        /// The value of the B function.
        /// </returns>
        private static double B(double T, double alpha)
        {
            return (1.0 - Math.Exp(-alpha * T)) / alpha;
        }

        #region IEstimationResultPopulable Members
        /*
        /// <summary>
        /// Populate editable fields from name and value vectors
        /// specific to HW.
        /// </summary>
        /// <param name="names">
        /// An array with the names of the variable,
        /// will search for alpha (or a1), sigma (or sigma1).
        /// </param>
        /// <param name="values">The values associated to the parameters in names.</param>
        public void Populate(string[] names, double[] values)
        {
            bool found = false;
            this.alpha1 = new ModelParameter(PopulateHelper.GetValue("alpha", "a1", names, values, out found), alphaDescription);
            this.sigma1 = new ModelParameter(PopulateHelper.GetValue("sigma", "sigma1", names, values, out found), sigmaDescription);
            this.lambda0 = new ModelParameter(PopulateHelper.GetValue("Lambda0", "lambda0", names, values, out found), lambda0Description);
        }
        */

        /// <summary>
        /// Populate editable fields from name and value vectors
        /// specific to the Heston extended process.
        /// </summary>
        /// <param name="stocProcess">
        /// The stochastic process which is being referenced to.
        /// </param>
        /// <param name="estimate">
        /// The estimation result which contains values and names of parameters.
        /// It will be searched for S0, kappa, theta, sigma, V0 and rho.
        /// </param>
        public void Populate(IStochasticProcess stocProcess, EstimationResult estimate)
        {
            bool found;
            this.alpha1 = new ModelParameter(PopulateHelper.GetValue("alpha", "a1", estimate.Names, estimate.Values, out found), alphaDescription);
            this.sigma1 = new ModelParameter(PopulateHelper.GetValue("sigma", "sigma1", estimate.Names, estimate.Values, out found), sigmaDescription);
            this.lambda0 = new ModelParameter(PopulateHelper.GetValue("Lambda0", "lambda0", estimate.Names, estimate.Values, out found), lambda0Description);

            if(estimate.Objects != null && estimate.Objects.Length > 0)
            {
                this.lowerBounds = new ModelParameterArray(estimate.Objects[0] as double[]);
                this.lowerBounds.Description = "Lower Bounds";
                this.upperBounds = new ModelParameterArray(estimate.Objects[1] as double[]);
                this.upperBounds.Description = "Upper Bounds";
                this.rmse_a = new ModelParameter((double)estimate.Objects[2], "RMSE Abs");
            }
        }
        #endregion

        #region IGreeksDerivativesInfo implementation
        /// <summary>
        /// Gets the factors for Delta Greek derivative.
        /// </summary>
        /// <returns>
        /// Null as the functionality is not implemented.
        /// </returns>
        public IModelParameter[] GetDeltaFactors()
        {
            // TODO: fixme how to handle short rate processes?
            return null;
        }

        /// <summary>
        /// Gets the factors for Vega Greek derivative.
        /// </summary>
        /// <returns>
        /// A model parameter containing the sigma value of HW.
        /// </returns>
        public IModelParameter[] GetVegaFactors()
        {
            return new IModelParameter[] { this.sigma1 };
        }
        #endregion

        #region IOpenCLCode implementation

        /// <summary>
        /// Gets the arguments needed for an OpenCL simulation.
        /// Alpha1, sigma1 and semidrift, driftAdjustment are used in this context.
        /// </summary>
        public List<Tuple<string, object>> Arguments
        {
            get
            {
                List<Tuple<string, object>> args = new List<Tuple<string, object>>();
                args.Add(new Tuple<string, object>("alpha1", this.alpha1));
                args.Add(new Tuple<string, object>("sigma1", this.sigma1));
                args.Add(new Tuple<string, object>("lambda0", this.lambda0));
                return args;
            }
        }

        /// <summary>
        /// Gets the OpenCL code used to calculate A and B.
        /// </summary>
        public Dictionary<string, string> Code
        {
            get
            {
                Dictionary<string, string> sources = new Dictionary<string, string>();
                sources.Add("B", "*b = sigma1;");
                sources.Add("A", "*a = -alpha1 * x[0] + lambda0 * sigma1;");
                return sources;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the plugin OpenCL implementation is usable.
        /// This plugin can always run through the OpenCL simulator so it always returns true.
        /// </summary>
        public bool IsOpenCLUsable
        {
            get
            {
                return true;
            }
        }
        #endregion

        /// <summary>
        /// Handles the conversion, after the simulation, from y to the short rate r.
        /// </summary>
        /// <param name="dates">Simulation dates.</param>
        /// <param name="outDynamic">The input and output components of the transformation.</param>
        public virtual void Transform(double[] dates, IMatrixSlice outDynamic)
        {
            
#if TFORWARDFORMULATION
            for (int j = 0; j < dates.Length; j++)
            {
                outDynamic[j, 0] = outDynamic[j, 0] + this.alphaT[j];
            }
#endif
             
        }


        public double ExpectedShortRate(double t)
        {
            double term1 = Math.Exp(-alpha1Temp * t) * x0[0];
            double ds=0.001;
            double term2=0;
            for (double s = 0; s <= t; s += ds)
                term2 += Math.Exp(alpha1Temp * (s - t)) * theta(s,ds) * ds;
            return term1+term2;
        }
        internal double ExpectedAverageRate(double t)
        {
            List<double> avg = new List<double>();
            double ds = 0.001;
            double term2 = 0;
            for (double s = 0; s <= t; s += ds)
            {
                double term1 = Math.Exp(-alpha1Temp * s) * ZR(0);
                term2 += Math.Exp(alpha1Temp * (s - t)) * theta(s, ds) * ds;
                avg.Add(term1 + term2);
            }
            var v = (Vector)(avg.ToArray());
            return v.Mean();
        }
    
        public void Simulate(double[] Dates, IReadOnlyMatrixSlice Noise, IMatrixSlice OutDynamic)
        {
            OutDynamic[0, 0] = x0[0];
            for (int i = 1; i < Dates.Length; i++)
            {
                double dt= Dates[i]-Dates[i-1];
                double rdt=Math.Sqrt(dt);
                double th = theta(Dates[i], dt);
                OutDynamic[i, 0] = OutDynamic[i-1, 0]+(th - alpha1Temp * OutDynamic[i - 1, 0]) * dt + sigma1Temp * Noise[i - 1, 0] * rdt;
            }
        }


    }


	
    

}
