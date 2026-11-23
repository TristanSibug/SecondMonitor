const featureDemo = document.querySelector('[data-feature-demo]');
const sceneTemplate = document.querySelector('#feature-scene');
const motion = window.matchMedia('(prefers-reduced-motion: no-preference)');
const tabList = document.createElement('div');
tabList.className = 'demo-tabs';
tabList.setAttribute('role', 'tablist');
tabList.setAttribute('aria-label', 'Second Monitor features');

const features = Array.from(featureDemo.children, (article) => {
  const mode = article.dataset.demo;
  const tab = document.createElement('button');
  const title = document.createElement('span');
  title.textContent = article.querySelector('h3').textContent;
  tab.type = 'button';
  tab.id = `feature-tab-${mode}`;
  tab.setAttribute('role', 'tab');
  tab.append(article.querySelector('.feature-icon'), title);
  tabList.append(tab);

  const panel = document.createElement('div');
  const copy = document.createElement('div');
  const play = document.createElement('button');
  const scene = sceneTemplate.content.firstElementChild.cloneNode(true);
  panel.className = 'demo-panel';
  panel.setAttribute('role', 'tabpanel');
  panel.setAttribute('aria-labelledby', tab.id);
  panel.tabIndex = 0;
  copy.className = 'demo-copy';
  play.type = 'button';
  play.className = 'demo-play';
  play.textContent = 'Play demo';
  scene.dataset.mode = mode;
  copy.append(article.querySelector('p'), play);
  panel.append(copy, scene);

  function playDemo() {
    if (!motion.matches) return;
    scene.classList.remove('is-playing');
    // Apply the reset before replaying the CSS animations.
    void scene.offsetWidth;
    scene.classList.add('is-playing');
  }

  play.addEventListener('click', playDemo);
  return { tab, panel, playDemo, scene };
});

let selectedIndex = 0;

function selectFeature(index, animate = true) {
  selectedIndex = index;
  for (const [position, feature] of features.entries()) {
    const selected = position === index;
    feature.tab.setAttribute('aria-selected', String(selected));
    feature.tab.tabIndex = selected ? 0 : -1;
    feature.panel.hidden = !selected;
    feature.scene.classList.remove('is-playing');
  }
  if (animate) features[index].playDemo();
}

for (const [index, feature] of features.entries()) {
  feature.tab.addEventListener('click', () => selectFeature(index));

  feature.tab.addEventListener('keydown', (event) => {
    let nextIndex;
    switch (event.key) {
      case 'ArrowLeft': nextIndex = (index + features.length - 1) % features.length; break;
      case 'ArrowRight': nextIndex = (index + 1) % features.length; break;
      case 'Home': nextIndex = 0; break;
      case 'End': nextIndex = features.length - 1; break;
      default: return;
    }
    event.preventDefault();
    feature.tab.tabIndex = -1;
    features[nextIndex].tab.tabIndex = 0;
    features[nextIndex].tab.focus();
  });
}

tabList.addEventListener('focusout', (event) => {
  if (tabList.contains(event.relatedTarget)) return;
  for (const [index, feature] of features.entries()) {
    feature.tab.tabIndex = index === selectedIndex ? 0 : -1;
  }
});

motion.addEventListener('change', () => {
  for (const { scene } of features) scene.classList.remove('is-playing');
});

selectFeature(0, false);
featureDemo.classList.add('demo-enhanced');
featureDemo.replaceChildren(tabList, ...features.map(({ panel }) => panel));
