﻿/*
 * Copyright 2019 Peter Han
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 * BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using Harmony;
using System;
using System.Collections.Generic;
using UnityEngine;

using BrightnessDict = System.Collections.Generic.IDictionary<int, float>;
using LightGridEmitter = LightGridManager.LightGridEmitter;
using IntHandle = HandleVector<int>.Handle;

namespace PeterHan.PLib.Lighting {
	/// <summary>
	/// Manages lighting. Instantiated only by the latest PLib version.
	/// </summary>
	internal sealed class PLightManager {
		/// <summary>
		/// The only instance of PLightManager.
		/// </summary>
		internal static PLightManager Instance { get; private set; }

		/// <summary>
		/// Replaces the scene partitioner method to register lights for tile changes in
		/// their active radius.
		/// </summary>
		/// <param name="instance">The light to register.</param>
		/// <param name="solidPart">The solid partitioner registered.</param>
		/// <param name="liquidPart">The liquid partitioner registered.</param>
		/// <returns>true if registered, or false if not.</returns>
		internal static bool AddScenePartitioner(Light2D instance, ref IntHandle solidPart,
				ref IntHandle liquidPart) {
			var trInstance = Traverse.Create(instance);
			bool handled = false;
			int rad = (int)instance.Range, cell = trInstance.GetProperty<int>("origin");
			// Only if there would be a valid area
			if (rad > 0 && Grid.IsValidCell(cell)) {
				var origin = Grid.CellToXY(cell);
				var minCoords = new Vector2I(origin.x - rad, origin.y - rad);
				// Optimize only for vanilla cone, rest get the whole thing
				int width = 2 * rad, height = (instance.shape == LightShape.Cone) ?
					rad : 2 * rad;
				solidPart = trInstance.CallMethod<IntHandle>("AddToLayer", minCoords,
					width, height, GameScenePartitioner.Instance.solidChangedLayer);
				liquidPart = trInstance.CallMethod<IntHandle>("AddToLayer", minCoords,
					width, height, GameScenePartitioner.Instance.liquidChangedLayer);
				handled = true;
			}
			return handled;
		}

		/// <summary>
		/// Creates and initializes the lighting manager instance.
		/// </summary>
		/// <returns>true if the lighting manager was initialized and has something to do,
		/// or false otherwise.</returns>
		internal static bool InitInstance() {
			object locker = PSharedData.GetData<object>(PRegistry.KEY_LIGHTING_LOCK);
			bool patch = false;
			if (locker != null)
				lock (locker) {
					// Only run if any lights were registered
					var list = PSharedData.GetData<List<object>>(PRegistry.KEY_LIGHTING_TABLE);
					if (list != null) {
						Instance = new PLightManager();
						Instance.Init(list);
						patch = true;
					}
				}
			return patch;
		}

		/// <summary>
		/// Converts a PLightShape into this mod's namespace.
		/// </summary>
		/// <param name="otherShape">The shape from the shared data.</param>
		/// <returns>An equivalent shape in this mod's namespace.</returns>
		private static PLightShape LightToInstance(object otherShape) {
			var shape = otherShape as PLightShape;
			if (shape == null) {
				var trLight = Traverse.Create(otherShape);
				// Retrieve the ID, handler, and identifier
				int id = trLight.GetProperty<int>("ShapeID");
				if (id > 0) {
					string identifer = trLight.GetProperty<string>("Identifier") ??
						("LightShape" + id);
					shape = new PLightShape(id, identifer, new CrossModLightWrapper(trLight).
						CastLight);
				} else
					// Some invalid object got in there somehow
					PUtil.LogWarning("Found light shape {0} with bad ID {1:D}!".F(otherShape,
						id));
			}
			return shape;
		}

		/// <summary>
		/// The game object which last requested lighting calculations.
		/// </summary>
		internal GameObject CallingObject { get; set; }

		/// <summary>
		/// The light brightness set by the last lighting brightness request.
		/// </summary>
		private readonly IDictionary<LightGridEmitter, CacheEntry> brightCache;

		/// <summary>
		/// The lighting shapes available, all in this mod's namespace.
		/// </summary>
		private readonly IList<PLightShape> shapes;

		private PLightManager() {
			if (Instance != null)
				PUtil.LogError("Multiple PLightManager created!");
			else
				PUtil.LogDebug("Created PLightManager");
			// Needs to be thread safe! Unfortunately ConcurrentDictionary is unavailable in
			// Unity .NET so we have to use locks
			brightCache = new Dictionary<LightGridEmitter, CacheEntry>(128);
			CallingObject = null;
			Instance = this;
			shapes = new List<PLightShape>(16);
		}

		/// <summary>
		/// Ends a call to lighting update initiated by CreateLight.
		/// </summary>
		/// <param name="source">The source of the light.</param>
		internal void DestroyLight(LightGridEmitter source) {
			if (source != null)
				lock (brightCache) {
					brightCache.Remove(source);
				}
		}

		/// <summary>
		/// Gets the brightness at a given cell for the specified light source.
		/// </summary>
		/// <param name="source">The source of the light.</param>
		/// <param name="location">The location to check.</param>
		/// <param name="result">The brightness there.</param>
		/// <returns>true if that brightness is valid, or false otherwise.</returns>
		internal bool GetBrightness(LightGridEmitter source, int location, out int result) {
			bool valid;
			CacheEntry cacheEntry;
			lock (brightCache) {
				// Shared access to the cache
				valid = brightCache.TryGetValue(source, out cacheEntry);
			}
			if (valid) {
				valid = cacheEntry.Intensity.TryGetValue(location, out float ratio);
				if (valid)
					result = Mathf.RoundToInt(cacheEntry.BaseLux * ratio);
				else {
#if DEBUG
					PUtil.LogDebug("Lighting request for invalid cell at {0:D}".F(location));
#endif
					result = 0;
				}
			} else {
#if DEBUG
				PUtil.LogDebug("Lighting request for invalid emitter at {0:D}".F(location));
#endif
				result = 0;
			}
			return valid;
		}

		/// <summary>
		/// Handles a lighting system call. Not intended to be used - exists as a fallback.
		/// </summary>
		/// <param name="cell">The origin cell.</param>
		/// <param name="visiblePoints">The location where lit points will be stored.</param>
		/// <param name="range">The light radius.</param>
		/// <param name="shape">The light shape.</param>
		/// <returns>true if the lighting was handled, or false otherwise.</returns>
		internal bool GetVisibleCells(int cell, IList<int> visiblePoints, int range,
				LightShape shape) {
			int index = shape - LightShape.Cone - 1;
			bool handled = false;
			if (index >= 0 && index < shapes.Count) {
				var ps = shapes[index];
				// Do what we can, this only is reachable through methods we have patched
				var lux = DictionaryPool<int, float, PLightManager>.Allocate();
#if DEBUG
				PUtil.LogWarning("Unpatched call to GetVisibleCells; use LightGridEmitter." +
					"UpdateLitCells instead.");
#endif
				ps.FillLight(CallingObject, cell, range, lux);
				// Intensity does not matter
				foreach (var point in lux)
					visiblePoints.Add(point.Key);
				lux.Recycle();
				handled = true;
			}
			return handled;
		}

		/// <summary>
		/// Fills in this light manager from the shared light shape list. Invoked after all
		/// mods have loaded.
		/// </summary>
		/// <param name="lightShapes">The shapes from the shared data.</param>
		private void Init(List<object> lightShapes) {
			int i = 0;
			foreach (var light in lightShapes)
				// Should only have instances of PLightShape from other mods
				if (light != null && light.GetType().Name == typeof(PLightShape).Name) {
					var ls = LightToInstance(light);
					if (ls != null) {
						// Verify that the light goes into the right slot
						int sid = ls.ShapeID;
						if (sid != ++i)
							PUtil.LogWarning("Light shape {0} has the wrong ID {1:D}!".F(ls,
								sid));
						shapes.Add(ls);
					}
				} else
					// Moe must clean it!
					PUtil.LogError("Foreign contaminant in PLightManager: " + (light == null ?
						"null" : light.GetType().FullName));
		}

		/// <summary>
		/// Creates the preview for a given light.
		/// 
		/// RadiationGridManager.CreatePreview has no references so no sense in patching that
		/// yet.
		/// </summary>
		/// <param name="origin">The starting cell.</param>
		/// <param name="radius">The light radius.</param>
		/// <param name="shape">The light shape.</param>
		/// <param name="lux">The base brightness in lux.</param>
		/// <returns>true if the lighting was handled, or false otherwise.</returns>
		internal bool PreviewLight(int origin, float radius, LightShape shape, int lux) {
			bool handled = false;
			if (shape != LightShape.Circle && shape != LightShape.Cone) {
				var cells = DictionaryPool<int, float, PLightManager>.Allocate();
				// Replicate the logic of the original one...
				int index = shape - LightShape.Cone - 1;
				if (index < shapes.Count) {
					// Found handler!
					shapes[index].Handler?.Invoke(CallingObject, origin, (int)radius, cells);
					foreach (var pair in cells) {
						int cell = pair.Key;
						if (Grid.IsValidCell(cell)) {
							// Allow any fraction, not just linear falloff
							int lightValue = (int)Math.Round(lux * pair.Value);
							LightGridManager.previewLightCells.Add(new Tuple<int, int>(cell,
								lightValue));
							LightGridManager.previewLux[cell] = lightValue;
						}
					}
					CallingObject = null;
					handled = true;
				}
				cells.Recycle();
			}
			return handled;
		}

		/// <summary>
		/// Updates the lit cells list.
		/// </summary>
		/// <param name="source">The source of the light.</param>
		/// <param name="state">The light emitter state.</param>
		/// <param name="litCells">The location where lit cells will be placed.</param>
		/// <returns>true if the lighting was handled, or false otherwise.</returns>
		internal bool UpdateLitCells(LightGridEmitter source, LightGridEmitter.State state,
				IList<int> litCells) {
			bool handled = false;
			int index;
			if (source == null)
				throw new ArgumentNullException("source");
			if ((index = state.shape - LightShape.Cone - 1) >= 0 && index < shapes.Count &&
					litCells != null) {
				var ps = shapes[index];
				CacheEntry cacheEntry;
				lock (brightCache) {
					// Look up in cache, in a thread safe way
					if (!brightCache.TryGetValue(source, out cacheEntry)) {
						cacheEntry = new CacheEntry(CallingObject, state.intensity);
						brightCache.Add(source, cacheEntry);
					}
				}
				// We have the proper owner
				ps.FillLight(cacheEntry.Owner, state.origin, (int)state.radius, cacheEntry.
					Intensity);
				foreach (var point in cacheEntry.Intensity)
					litCells.Add(point.Key);
				handled = true;
			}
			return handled;
		}

		/// <summary>
		/// A cache entry in the light brightness cache.
		/// </summary>
		private sealed class CacheEntry {
			/// <summary>
			/// The base intensity in lux.
			/// </summary>
			internal int BaseLux { get; }

			/// <summary>
			/// The relative brightness per cell.
			/// </summary>
			internal BrightnessDict Intensity { get; }

			/// <summary>
			/// The owner which initiated the lighting call.
			/// </summary>
			internal GameObject Owner { get; }

			internal CacheEntry(GameObject owner, int baseLux) {
				BaseLux = baseLux;
				// Do not use the pool because these might last a long time and be numerous
				Intensity = new Dictionary<int, float>(64);
				Owner = owner;
			}

			public override string ToString() {
				return "Lighting Cache Entry for " + Owner?.name;
			}
		}

		/// <summary>
		/// Wraps a lighting system call from another mod's namespace.
		/// </summary>
		private sealed class CrossModLightWrapper {
			/// <summary>
			/// The method to call when lighting system handling is requested.
			/// </summary>
			private readonly Traverse method;

			internal CrossModLightWrapper(Traverse other) {
				method = other?.Method("Invoke", typeof(GameObject), typeof(int), typeof(int),
					typeof(BrightnessDict));
				if (method == null)
					PUtil.LogError("PLightSource handler has invalid method signature!");
			}

			/// <summary>
			/// Handles lighting for this light type.
			/// </summary>
			/// <param name="source">The source game object.</param>
			/// <param name="sourceCell">The cell casting the light.</param>
			/// <param name="range">The range of the light as set in the source.</param>
			/// <param name="brightness">The location where points lit by this light shape
			/// should be stored. The key is the cell to light, the value is the fraction of
			/// the base brightness to use here.</param>
			internal void CastLight(GameObject source, int sourceCell, int range,
					BrightnessDict brightness) {
				method?.GetValue(source, sourceCell, range, brightness);
			}
		}
	}
}