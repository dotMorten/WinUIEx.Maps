const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const root = path.resolve(__dirname, '..', '..');
const source = fs.readFileSync(path.join(root, 'MapSample', 'Samples', 'Maps', 'ComparisonWebMapControl.cs'), 'utf8');
const version = source.match(/RoadStyleVersion = "([^"]+)"/)[1];
const script = source.match(/string script = FormattableString.Invariant\(\$\$"""([\s\S]*?)"""\)/)[1]
    .replaceAll('{{position.Longitude}}', '-122.33')
    .replaceAll('{{position.Latitude}}', '47.61')
    .replaceAll('{{ZoomLevel}}', '9')
    .replaceAll('{{RoadStyleVersion}}', version);

test('comparison requests the same style revision and API as the native renderer', () => {
    const native = fs.readFileSync(path.join(root, 'WinUIEx.Maps', 'Rendering', 'VectorStyle.cs'), 'utf8');
    const query = new URLSearchParams(native.match(/const string query = "([^"]+)"/)[1]);
    assert.equal(version, query.get('styleVersion'));
    assert.ok(script.includes(`styleAPIVersion: "${query.get('api-version')}"`));
});

for (const exists of [true, false]) {
    for (const ready of [true, false]) {
        for (const rounding of ['none', 'observed', 'excessive']) {
            test(`initialize: map exists=${exists}, ready=${ready}, rounding=${rounding}`, () => {
                const messages = [];
                const handlers = {};
                let camera = { center: [0, 0], zoom: 3 };
                let pending;
                let serviceOptions;
                const map = {
                    setServiceOptions(value) { serviceOptions = value; },
                    events: {
                        addOnce(name, action) {
                            assert.equal(name, 'ready');
                            assert.equal(serviceOptions.styleDefinitionsVersion, version);
                            assert.equal(serviceOptions.styleAPIVersion, '2.0');
                            pending = action;
                            if (ready) action();
                        },
                        add(name, action) { handlers[name] = action; }
                    },
                    setCamera(value) {
                        camera = value;
                        if (rounding === 'observed') {
                            camera = { ...value, center: [-122.33001708984375, 47.609866530037976] };
                        } else if (rounding === 'excessive') {
                            camera = { ...value, center: [-120, 47.61] };
                        }
                        handlers.move?.();
                    },
                    getCamera() { return camera; }
                };
                const context = { chrome: { webview: { postMessage(message) { messages.push(message); } } } };
                context.window = context;
                context.initializeMap = () => { context.map = map; };
                if (exists) context.map = map;
                vm.runInNewContext(script, context);
                if (!exists) context.initializeMap();
                if (!ready) {
                    assert.equal(messages.length, 0);
                    pending();
                }
                assert.equal(messages[0].success, rounding !== 'excessive');
                assert.equal(camera.zoom, 9);
                if (rounding === 'none') {
                    assert.equal(camera.center[0], -122.33);
                    assert.equal(camera.center[1], 47.61);
                }
                context.comparisonSetCamera({ center: [-120, 48], zoom: 12, bearing: 25, pitch: 30 });
                assert.equal(messages.length, 1, 'Programmatic update must not echo to native');
                map.setCamera({ center: [-121, 49], zoom: 13, bearing: 40, pitch: 50 });
                assert.equal(messages[1].type, 'comparison-camera');
                assert.equal(messages[1].zoom, 13);
                assert.equal(messages[1].heading, 40);
                assert.equal(messages[1].pitch, 50);
            });
        }
    }
}

test('style configuration failure is reported instead of claiming readiness', () => {
    const messages = [];
    const context = {
        map: { setServiceOptions() { throw new Error('Configuration failed'); } },
        chrome: { webview: { postMessage(message) { messages.push(message); } } }
    };
    context.window = context;
    vm.runInNewContext(script, context);
    assert.equal(messages.length, 1);
    assert.equal(messages[0].success, false);
    assert.equal(messages[0].stage, 'hook');
});
